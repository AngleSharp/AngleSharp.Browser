namespace AngleSharp.Browser;

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System;
using System.Collections.Generic;

/// <summary>
/// The addresses of the documents an engine has shown, which links match <c>:visited</c>
/// for (AngleSharp itself never treats a link as visited). Like in current browsers the
/// visits are partitioned by the origin of the top-level page they happened in, so a page
/// can't learn of the user's history elsewhere by looking at the style of its links.
/// </summary>
sealed class VisitedLinks
{
    private readonly HashSet<(String Partition, String Address)> _visits = [];
    private readonly Object _sync = new();

    /// <summary>
    /// Records the visit of the given document, shown in the given top-level page (the
    /// document itself, if it is the top-level page).
    /// </summary>
    public void Record(IDocument document, IDocument? topLevel)
    {
        var address = Normalize(document.Url);

        if (address is null)
        {
            return;
        }

        lock (_sync)
        {
            _visits.Add((PartitionOf(document), address));

            if (topLevel is not null && topLevel != document)
            {
                _visits.Add((PartitionOf(topLevel), address));
            }
        }
    }

    /// <summary>
    /// Checks if the given element is a link to a document visited in the top-level page
    /// it is shown in.
    /// </summary>
    public Boolean IsVisited(IElement element, IDocument? topLevel)
    {
        var href = element switch
        {
            IHtmlAnchorElement anchor when anchor.HasAttribute("href") => anchor.Href,
            IHtmlAreaElement area when area.HasAttribute("href") => area.Href,
            _ => null,
        };
        var address = Normalize(href);

        if (address is null || (topLevel ?? element.Owner) is not { } page)
        {
            return false;
        }

        lock (_sync)
        {
            return _visits.Contains((PartitionOf(page), address));
        }
    }

    /// <summary>
    /// Registers <c>:link</c> and <c>:visited</c> for the visits of this list.
    /// </summary>
    public IPseudoClassSelectorFactory CreateSelectors(Func<IDocument?> topLevel)
    {
        var factory = new DefaultPseudoClassSelectorFactory();
        factory.Unregister(PseudoClassNames.Visited);
        factory.Unregister(PseudoClassNames.Link);
        factory.Register(PseudoClassNames.Visited, new LinkSelector(PseudoClassNames.Visited, el => IsVisited(el, topLevel())));
        factory.Register(PseudoClassNames.Link, new LinkSelector(PseudoClassNames.Link, el => el.IsLink() && !IsVisited(el, topLevel())));
        return factory;
    }

    private static String PartitionOf(IDocument document) => document.Origin ?? String.Empty;

    private static String? Normalize(String? address)
    {
        if (String.IsNullOrEmpty(address))
        {
            return null;
        }

        var url = new Url(address!);

        if (url.IsInvalid || (url.Scheme != "http" && url.Scheme != "https" && url.Scheme != "file"))
        {
            return null;
        }

        // the fragment doesn't make another page
        url.Fragment = null;
        return url.Href;
    }

    private sealed class LinkSelector(String name, Predicate<IElement> matches) : ISelector
    {
        public String Text => PseudoClassNames.Separator + name;

        public Priority Specificity => Priority.OneClass;

        public void Accept(ISelectorVisitor visitor) => visitor.PseudoClass(name);

        public Boolean Match(IElement element, IElement? scope) => matches(element);
    }
}
