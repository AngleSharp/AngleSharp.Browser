namespace Vision.Pages;

using System;
using AngleSharp.Browser;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Dom.Geometry;
using AngleSharp.Html.Dom;

/// <summary>
/// The inspector: shows the inspected engine's page as AngleSharp sees it right now, i.e., its
/// DOM, its CSSOM (the style sheets and their rules) and its render tree (the nodes taking part
/// in rendering with their computed styles and boxes). The views are built on the event loop of
/// the inspected engine, as it owns the DOM, and shown on the one of this page; only the rows
/// that are expanded are looked at.
/// </summary>
public sealed class InspectorPage : HtmlPage
{
    private static readonly TimeSpan MinRefreshDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] Tabs = ["dom", "cssom", "render"];
    private const int MaxRows = 3000;
    private const int ChildrenPage = 200;
    private const int MaxMatchedRules = 40;
    private const int MaxIndent = 28;

    private readonly BrowserEngine _target;
    private readonly Timer _timer;
    private readonly Dictionary<string, string> _shown = [];
    // what the user picked, changed on the event loop of this page and read on the inspected one
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<int>> _expanded = Tabs.ToDictionary(m => m, _ => new HashSet<int>());
    private readonly Dictionary<int, int> _childLimits = [];
    private string _tab = Tabs[0];
    private string _filter = string.Empty;
    private int? _selectedNode;
    private int? _selectedRule;
    // the identities of what the rows show, which only the inspected event loop touches
    private ConditionalWeakTable<object, object> _ids = [];
    private readonly Dictionary<int, object> _objects = [];
    private IDocument? _document;
    private int _nextId;
    private int _isScheduled;
    private int _isRefreshing;
    private int _needsRefresh;
    private long _refreshDelayTicks = MinRefreshDelay.Ticks;
    private volatile bool _isDisposed;

    public InspectorPage(string? colorScheme, BrowserEngine target)
        : base("inspector", colorScheme)
    {
        _target = target;
        _timer = new Timer(_ => _ = RefreshAsync());
        _target.Invalidated += OnTargetChanged;
        _target.StateChanged += OnTargetChanged;
    }

    public override void Dispose()
    {
        _isDisposed = true;
        _target.Invalidated -= OnTargetChanged;
        _target.StateChanged -= OnTargetChanged;
        _timer.Dispose();
        base.Dispose();
    }

    protected override void Wire(IDocument document)
    {
        foreach (var tab in Tabs)
        {
            OnClick(document, $"tab-{tab}", () =>
            {
                lock (_lock)
                {
                    _tab = tab;
                }

                foreach (var other in Tabs)
                {
                    document.GetElementById($"tab-{other}")?.ClassList.ToggleToken("active", other == tab);
                }

                RefreshNow();
            });
        }

        document.GetElementById("tree")?.AddEventListener("click", (_, ev) => OnTreeClick(ev.OriginalTarget as IElement));
        document.GetElementById("filter")?.AddEventListener("input", (_, _) =>
        {
            lock (_lock)
            {
                _filter = GetValue(document, "filter").Trim();
            }

            RefreshNow();
        });
        OnClick(document, "refresh", RefreshNow);
        RefreshNow();
    }

    private void OnTreeClick(IElement? target)
    {
        if (target?.Closest("[data-id]") is not { } row || !int.TryParse(row.GetAttribute("data-id"), out var id))
        {
            return;
        }

        lock (_lock)
        {
            if (row.HasAttribute("data-more"))
            {
                _childLimits[id] = _childLimits.GetValueOrDefault(id, ChildrenPage) + ChildrenPage;
            }
            else if (target.Closest(".twisty") is not null)
            {
                var expanded = _expanded[_tab];

                if (!expanded.Remove(id))
                {
                    expanded.Add(id);
                }
            }
            else if (_tab == "cssom")
            {
                _selectedRule = id;
            }
            else
            {
                _selectedNode = id;
            }
        }

        RefreshNow();
    }

    private void OnTargetChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed && Interlocked.Exchange(ref _isScheduled, 1) == 0)
        {
            _timer.Change(TimeSpan.FromTicks(Volatile.Read(ref _refreshDelayTicks)), Timeout.InfiniteTimeSpan);
        }
    }

    private void RefreshNow()
    {
        if (!_isDisposed)
        {
            Interlocked.Exchange(ref _isScheduled, 1);
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RefreshAsync()
    {
        Volatile.Write(ref _isScheduled, 0);

        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            Volatile.Write(ref _needsRefresh, 1);
            return;
        }

        try
        {
            View? view;

            try
            {
                using var timeout = new CancellationTokenSource(BuildTimeout);
                view = await _target.InvokeAsync(Build, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // the page is busy; it invalidates itself once it has changed
                view = null;
            }

            if (view is not null)
            {
                // looking at a page should not slow it down noticeably, however large it is
                Volatile.Write(ref _refreshDelayTicks, Math.Max(MinRefreshDelay.Ticks, view.BuildTime.Ticks * 10));
                await Engine.InvokeAsync(document =>
                {
                    if (document is not null)
                    {
                        Show(document, view);
                    }
                });
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Rendering the inspector failed: {ex}");
        }
        finally
        {
            Volatile.Write(ref _isRefreshing, 0);

            if (Interlocked.Exchange(ref _needsRefresh, 0) == 1)
            {
                RefreshNow();
            }
        }
    }

    private void Show(IDocument document, View view)
    {
        Replace(document, "tree", view.Tree);
        Replace(document, "details-top", view.Details);
        Replace(document, "computed", view.Computed);
        document.GetElementById("computed-section")?.ClassList.ToggleToken("active", view.HasComputed);
    }

    private void Replace(IDocument document, string id, string html)
    {
        // comparing with what was set, as the serialized DOM differs from the source
        if ((!_shown.TryGetValue(id, out var current) || current != html) && document.GetElementById(id) is { } element)
        {
            _shown[id] = html;
            element.InnerHtml = html;
        }
    }

    /// <summary>
    /// Builds what the current tab shows. Runs on the event loop of the inspected engine.
    /// </summary>
    private View Build(IDocument? document)
    {
        var start = Stopwatch.GetTimestamp();
        Selection selection;

        if (!ReferenceEquals(document, _document))
        {
            Reset(document);
        }

        lock (_lock)
        {
            selection = new Selection(_tab, [.. _expanded[_tab]], new Dictionary<int, int>(_childLimits), _selectedNode, _selectedRule, _filter);
        }

        if (document is null)
        {
            return new View(Empty("No page loaded."), string.Empty, string.Empty, false, TimeSpan.Zero);
        }

        var builder = new TreeBuilder(this, selection);
        var tree = string.Empty;
        var details = string.Empty;
        var computed = string.Empty;
        var hasComputed = false;

        try
        {
            tree = selection.Tab switch
            {
                "cssom" => builder.StyleSheets(document),
                "render" => builder.RenderTree(document),
                _ => builder.Dom(document),
            };

            if (selection.Tab == "cssom")
            {
                details = Find(selection.SelectedRule) is ICssRule rule
                    ? RuleDetails(document, rule)
                    : Empty("Select a rule to see all of it.");
            }
            else
            {
                switch (Find(selection.SelectedNode))
                {
                    case IElement element:
                        details = ElementDetails(element);
                        computed = ComputedStyle(element, selection.Filter);
                        hasComputed = true;
                        break;
                    case INode node:
                        details = NodeDetails(node);
                        break;
                    default:
                        details = Empty("Select a node to see its attributes, box and styles.");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the inspector is for pages in any state, and one failing to style or lay out is one of them
            tree = tree.Length > 0 ? tree : Empty($"This view failed: {ex.Message}");
            details = details.Length > 0 ? details : Empty($"The details failed: {ex.Message}");
        }

        return new View(tree, details, computed, hasComputed, Stopwatch.GetElapsedTime(start));
    }

    private void Reset(IDocument? document)
    {
        _document = document;
        _ids = [];
        _objects.Clear();

        lock (_lock)
        {
            foreach (var expanded in _expanded.Values)
            {
                expanded.Clear();
            }

            _childLimits.Clear();
            _selectedNode = null;
            _selectedRule = null;

            // the document's outline is what everybody looks at first
            if (document?.DocumentElement is { } root)
            {
                _expanded["dom"].Add(IdOf(root));
                _expanded["render"].Add(IdOf(root));

                if (document.Body is { } body)
                {
                    _expanded["dom"].Add(IdOf(body));
                    _expanded["render"].Add(IdOf(body));
                }
            }
        }
    }

    private int IdOf(object item)
    {
        var id = (int)_ids.GetValue(item, _ => ++_nextId);
        _objects[id] = item;
        return id;
    }

    private object? Find(int? id) => id is int key ? _objects.GetValueOrDefault(key) : null;

    private static string Empty(string text) => $"""<p class="empty">{Encode(text)}</p>""";

    private static string Section(string title, string body) => $"""<h2 class="section">{Encode(title)}</h2>{body}""";

    private static string Pair(string name, string value) =>
        $"""<div class="pair"><span class="name">{Encode(name)}</span><span class="value">{Encode(value)}</span></div>""";

    private static string Shorten(string text, int length = 80)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > length ? $"{line[..(length - 1)]}…" : line;
    }

    private static string Describe(IElement element)
    {
        var id = element.Id is { Length: > 0 } value ? $"#{value}" : string.Empty;
        var classes = element.ClassList.Length > 0 ? $".{string.Join('.', element.ClassList.Take(3))}" : string.Empty;
        return $"{element.LocalName}{id}{classes}";
    }

    private static string FormatBox(IDomRect rect) => $"{rect.Width:0.##} × {rect.Height:0.##}";

    private static string ElementDetails(IElement element)
    {
        var html = new StringBuilder();
        var path = element.GetAncestors().OfType<IElement>().Reverse().Append(element).Select(Describe);
        html.Append($"""<div class="path">{Encode(string.Join(" › ", path))}</div>""");

        var attributes = string.Concat(element.Attributes.Select(m => Pair(m.Name, m.Value)));
        html.Append(Section("Attributes", attributes.Length > 0 ? attributes : """<p class="none">None</p>"""));
        html.Append(Section("Box", BoxModel(element)));
        html.Append(Section("Matched rules", MatchedRules(element)));
        return html.ToString();
    }

    private static string BoxModel(IElement element)
    {
        try
        {
            var rect = element.GetBoundingClientRect();
            return string.Concat(
                Pair("Position", $"{rect.Left:0.##}, {rect.Top:0.##}"),
                Pair("Size", FormatBox(rect)),
                Pair("Client size", $"{element.GetClientWidth()} × {element.GetClientHeight()}"),
                Pair("Scroll size", $"{element.GetScrollWidth()} × {element.GetScrollHeight()}"));
        }
        catch (Exception ex)
        {
            return $"""<p class="none">Not available: {Encode(ex.Message)}</p>""";
        }
    }

    private static string MatchedRules(IElement element)
    {
        var html = new StringBuilder();

        if (element.GetAttribute("style") is { Length: > 0 } inline)
        {
            html.Append(Rule("element.style", null, inline));
        }

        if (element.Owner?.DefaultView is not { } window || element.Owner.Context.GetService<IRenderDevice>() is not { } device)
        {
            return html.ToString();
        }

        var scope = element.Owner.DocumentElement;
        var matches = new List<(ICssStyleRule Rule, Priority Priority, int Order)>();

        foreach (var rule in window.GetStyleCollection(device))
        {
            if (rule.TryMatch(element, scope, out var priority))
            {
                matches.Add((rule, priority, matches.Count));
            }
        }

        // what wins comes first, like in the cascade
        foreach (var match in matches.OrderByDescending(m => m.Priority).ThenByDescending(m => m.Order).Take(MaxMatchedRules))
        {
            html.Append(Rule(match.Rule.SelectorText, SheetName(match.Rule.Owner), match.Rule.Style.CssText));
        }

        if (matches.Count > MaxMatchedRules)
        {
            html.Append($"""<p class="none">… and {matches.Count - MaxMatchedRules} more</p>""");
        }

        return html.Length > 0 ? html.ToString() : """<p class="none">None</p>""";
    }

    private static string Rule(string selector, string? source, string declarations)
    {
        var from = source is null ? string.Empty : $"""<div class="source">{Encode(source)}</div>""";
        var lines = declarations.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return $"""<div class="rule"><div class="selector">{Encode(selector)}</div>{from}<pre class="code">{Encode(string.Join(";\n", lines))}</pre></div>""";
    }

    private static string ComputedStyle(IElement element, string filter)
    {
        var style = element.ComputeCurrentStyle();
        var properties = style
            .Where(m => filter.Length == 0 || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (m.Value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => Pair(m.Name, m.Value ?? string.Empty));
        var html = string.Concat(properties);
        return html.Length > 0 ? html : """<p class="none">No properties match.</p>""";
    }

    private static string NodeDetails(INode node) =>
        Section(node.NodeName, $"""<pre class="code">{Encode(node.TextContent ?? (node as IDocumentType)?.Name ?? string.Empty)}</pre>""");

    private static string RuleDetails(IDocument document, ICssRule rule)
    {
        var html = new StringBuilder();
        html.Append($"""<div class="path">{Encode(rule.Owner is { } sheet ? SheetName(sheet) : "(no sheet)")}</div>""");

        if (rule is ICssStyleRule style)
        {
            html.Append(Pair("Matches", CountMatches(document, style.SelectorText)));
        }

        html.Append(Section("Rule", $"""<pre class="code">{Encode(rule.CssText)}</pre>"""));
        return html.ToString();
    }

    private static string CountMatches(IDocument document, string selector)
    {
        try
        {
            var count = document.QuerySelectorAll(selector).Length;
            return count == 1 ? "1 element" : $"{count} elements";
        }
        catch (Exception)
        {
            return "Unknown (the selector is not supported)";
        }
    }

    private static string SheetName(IStyleSheet? sheet)
    {
        if (sheet is null)
        {
            return "(no sheet)";
        }

        if (!string.IsNullOrEmpty(sheet.Href))
        {
            return Uri.TryCreate(sheet.Href, UriKind.Absolute, out var uri) ? uri.Segments.Last() : sheet.Href;
        }

        return sheet.OwnerNode is null ? "User agent" : "<style>";
    }

    private sealed record Selection(string Tab, HashSet<int> Expanded, Dictionary<int, int> ChildLimits, int? SelectedNode, int? SelectedRule, string Filter);

    private sealed record View(string Tree, string Details, string Computed, bool HasComputed, TimeSpan BuildTime);

    /// <summary>
    /// Writes the rows of one view, as far as they are expanded.
    /// </summary>
    private sealed class TreeBuilder(InspectorPage page, Selection selection)
    {
        private readonly StringBuilder _html = new();
        private int _rows;

        public string Dom(IDocument document)
        {
            foreach (var child in document.ChildNodes)
            {
                AppendNode(child, 0);
            }

            return Finish("The document is empty.");
        }

        public string StyleSheets(IDocument document)
        {
            foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
            {
                AppendSheet(sheet, 0);
            }

            return Finish("This page has no style sheets.");
        }

        public string RenderTree(IDocument document)
        {
            if (document.DocumentElement is null || document.DefaultView is not { } window)
            {
                return Empty("There is nothing to render.");
            }

            AppendRenderNode(window.Render(), 0);
            return Finish("There is nothing to render.");
        }

        private string Finish(string empty)
        {
            if (_rows >= MaxRows)
            {
                _html.Append($"""<p class="none">Only the first {MaxRows} rows are shown; collapse some.</p>""");
            }

            return _html.Length > 0 ? _html.ToString() : Empty(empty);
        }

        private void AppendNode(INode node, int depth)
        {
            switch (node)
            {
                case IElement element:
                    AppendElement(element, depth);
                    break;
                case IText text when !string.IsNullOrWhiteSpace(text.Data):
                    AppendRow(text, depth, false, $"""<span class="text">"{Encode(Shorten(text.Data))}"</span>""");
                    break;
                case IComment comment:
                    AppendRow(comment, depth, false, $"""<span class="comment">&lt;!-- {Encode(Shorten(comment.Data))} --&gt;</span>""");
                    break;
                case IDocumentType type:
                    AppendRow(type, depth, false, $"""<span class="comment">&lt;!doctype {Encode(type.Name)}&gt;</span>""");
                    break;
            }
        }

        private void AppendElement(IElement element, int depth)
        {
            var children = GetChildren(element);

            // a bit of text is shown right away instead of being a level of its own
            if (children.Count == 1 && children[0] is IText { Data.Length: <= 60 } only)
            {
                AppendRow(element, depth, false, $"""{OpeningTag(element)}<span class="text">{Encode(only.Data.Trim())}</span><span class="punct">&lt;/</span><span class="tag">{Encode(element.LocalName)}</span><span class="punct">&gt;</span>""");
                return;
            }

            var isExpanded = AppendRow(element, depth, children.Count > 0, OpeningTag(element));

            if (isExpanded)
            {
                AppendChildren(element, children, depth, AppendNode);
            }
        }

        /// <summary>
        /// The children of the element, as well as what hangs off it: the document of an iframe
        /// and the shadow tree.
        /// </summary>
        private static List<INode> GetChildren(IElement element)
        {
            var children = new List<INode>();

            if (element.ShadowRoot is { } shadow)
            {
                children.AddRange(shadow.ChildNodes);
            }

            children.AddRange(element.ChildNodes.Where(m => m is not IText text || !string.IsNullOrWhiteSpace(text.Data)));

            if (element is IHtmlInlineFrameElement { ContentDocument: { } frame })
            {
                children.AddRange(frame.ChildNodes);
            }

            return children;
        }

        private static string OpeningTag(IElement element)
        {
            var html = new StringBuilder($"""<span class="punct">&lt;</span><span class="tag">{Encode(element.LocalName)}</span>""");

            foreach (var attribute in element.Attributes)
            {
                html.Append($""" <span class="attr">{Encode(attribute.Name)}</span><span class="punct">=</span><span class="val">"{Encode(Shorten(attribute.Value, 60))}"</span>""");
            }

            return html.Append("""<span class="punct">&gt;</span>""").ToString();
        }

        private void AppendRenderNode(IRenderNode node, int depth)
        {
            switch (node)
            {
                // AngleSharp.Css keeps them in its tree, but nothing about them gets rendered
                case ElementRenderNode element when element.ComputedStyle.GetPropertyValue("display") == "none":
                    break;
                case ElementRenderNode element:
                    var children = element.Children.Where(IsRendered).ToList();
                    var isExpanded = AppendRow(element.Ref, depth, children.Count > 0, RenderLabel(element));

                    if (isExpanded)
                    {
                        AppendChildren(element.Ref, children, depth, AppendRenderNode);
                    }

                    break;
                case TextRenderNode text when !string.IsNullOrWhiteSpace(text.Ref.Data):
                    AppendRow(text.Ref, depth, false, $"""<span class="text">"{Encode(Shorten(text.Ref.Data))}"</span>""");
                    break;
            }
        }

        private static bool IsRendered(IRenderNode node) => node switch
        {
            ElementRenderNode element => element.ComputedStyle.GetPropertyValue("display") != "none",
            TextRenderNode text => !string.IsNullOrWhiteSpace(text.Ref.Data),
            _ => true,
        };

        private static string RenderLabel(ElementRenderNode node)
        {
            var html = new StringBuilder($"""<span class="tag">{Encode(Shorten(Describe(node.Ref), 32))}</span>""");
            var style = node.ComputedStyle;

            if (style.GetPropertyValue("display") is { Length: > 0 } display)
            {
                html.Append($""" <span class="chip">{Encode(display)}</span>""");
            }

            if (style.GetPropertyValue("position") is { Length: > 0 } position and not "static")
            {
                html.Append($""" <span class="chip">{Encode(position)}</span>""");
            }

            try
            {
                html.Append($""" <span class="size">{Encode(FormatBox(node.Ref.GetBoundingClientRect()))}</span>""");
            }
            catch (Exception)
            {
                // the node has no box (yet), which the row just does not tell
            }

            return html.ToString();
        }

        private void AppendSheet(ICssStyleSheet sheet, int depth)
        {
            var media = sheet.Media.MediaText is { Length: > 0 } text ? $""" <span class="chip">{Encode(text)}</span>""" : string.Empty;
            var disabled = sheet.IsDisabled ? """ <span class="chip">disabled</span>""" : string.Empty;
            var count = sheet.Rules.Length;
            var isExpanded = AppendRow(sheet, depth, count > 0, $"""<span class="tag">{Encode(SheetName(sheet))}</span>{media}{disabled} <span class="size">{count} rules</span>""");

            if (isExpanded)
            {
                AppendChildren(sheet, sheet.Rules.ToList(), depth, AppendRule);
            }
        }

        private void AppendRule(ICssRule rule, int depth)
        {
            var (label, children) = rule switch
            {
                ICssStyleRule style => ($"""<span class="selector">{Encode(Shorten(style.SelectorText))}</span>""", style.Rules.ToList()),
                ICssMediaRule media => ($"""<span class="at">@media</span> {Encode(Shorten(media.ConditionText))}""", media.Rules.ToList()),
                ICssGroupingRule group => ($"""<span class="at">{Encode(group.Type.ToString())}</span>""", group.Rules.ToList()),
                ICssKeyframesRule frames => ($"""<span class="at">@keyframes</span> {Encode(frames.Name)}""", frames.Rules.ToList()),
                _ => ($"""<span class="at">{Encode(Shorten(rule.CssText))}</span>""", []),
            };

            if (AppendRow(rule, depth, children.Count > 0, label))
            {
                AppendChildren(rule, children, depth, AppendRule);
            }
        }

        private void AppendChildren<T>(object parent, IReadOnlyList<T> children, int depth, Action<T, int> append)
        {
            var id = page.IdOf(parent);
            var limit = selection.ChildLimits.GetValueOrDefault(id, ChildrenPage);

            foreach (var child in children.Take(limit))
            {
                append(child, depth + 1);
            }

            if (children.Count > limit && _rows < MaxRows)
            {
                _html.Append($"""<div class="row more" data-id="{id}" data-more="1" style="padding-left:{Indent(depth + 1)}px">Show more ({children.Count - limit} left)</div>""");
            }
        }

        /// <summary>
        /// Writes the row and returns if its children follow.
        /// </summary>
        private bool AppendRow(object item, int depth, bool hasChildren, string label)
        {
            if (_rows >= MaxRows)
            {
                return false;
            }

            _rows++;
            var id = page.IdOf(item);
            var isExpanded = hasChildren && selection.Expanded.Contains(id);
            var selected = selection.Tab == "cssom" ? selection.SelectedRule : selection.SelectedNode;
            var twisty = hasChildren ? (isExpanded ? "▾" : "▸") : string.Empty;
            _html.Append($"""<div class="row{(selected == id ? " selected" : "")}" data-id="{id}" style="padding-left:{Indent(depth)}px"><span class="twisty">{twisty}</span>{label}</div>""");
            return isExpanded;
        }

        private static int Indent(int depth) => 4 + Math.Min(depth, MaxIndent) * 12;
    }
}
