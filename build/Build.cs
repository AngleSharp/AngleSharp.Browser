using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Tools.GitHub;
using Fallout.Common.Utilities.Collections;
using Fallout.Solutions;
using Octokit;
using Octokit.Internal;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Fallout.Common.Tools.DotNet.DotNetTasks;
using Project = Fallout.Solutions.Project;

class Build : FalloutBuild
{
    public static int Main () => Execute<Build>(x => x.RunUnitTests);

    [Fallout.Common.Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Fallout.Common.Parameter("ReleaseNotesFilePath - To determine the SemanticVersion")]
    readonly AbsolutePath ReleaseNotesFilePath = RootDirectory / "CHANGELOG.md";

    [Solution]
    readonly Solution Solution;

    string TargetProjectName => "AngleSharp.Browser";

    // Every project shipped as a NuGet package, which all share one version.
    string[] TargetLibNames => [TargetProjectName, $"{TargetProjectName}.Avalonia"];

    AbsolutePath SourceDirectory => RootDirectory / "src";

    AbsolutePath ResultDirectory => RootDirectory / "bin" / Version;

    AbsolutePath NugetDirectory => ResultDirectory / "nuget";

    GitHubActions GitHubActions => GitHubActions.Instance;

    IReadOnlyList<Project> TargetProjects { get; set; }

    // Note: The built-in ChangeLogTasks (inherited from NUKE) look buggy. So using the Cake source code.
    IReadOnlyList<ReleaseNotes> ChangeLog { get; set; }

    ReleaseNotes LatestReleaseNotes { get; set; }

    SemVersion SemVersion { get; set; }

    string Version { get; set; }

    protected override void OnBuildInitialized()
    {
        var parser = new ReleaseNotesParser();

        Log.Debug("Reading ChangeLog {FilePath}...", ReleaseNotesFilePath);
        ChangeLog = parser.Parse(File.ReadAllText(ReleaseNotesFilePath));
        ChangeLog.NotNull("ChangeLog / ReleaseNotes could not be read!");

        LatestReleaseNotes = ChangeLog.First();
        LatestReleaseNotes.NotNull("LatestVersion could not be read!");

        Log.Debug("Using LastestVersion from ChangeLog: {LatestVersion}", LatestReleaseNotes.Version);
        SemVersion = LatestReleaseNotes.SemVersion;
        Version = LatestReleaseNotes.Version.ToString();

        if (GitHubActions != null)
        {
            Log.Debug("Add Version Postfix if under CI - GithubAction(s)...");

            var buildNumber = GitHubActions.RunNumber;

            if (ScheduledTargets.Contains(Default))
            {
                Version = $"{Version}-ci.{buildNumber}";
            }
            else if (ScheduledTargets.Contains(PrePublish))
            {
                Version = $"{Version}-beta.{buildNumber}";
            }
        }

        Log.Information("Building version: {Version}", Version);

        TargetProjects = TargetLibNames.Select(name => Solution.GetProject(name).NotNull($"{name} could not be loaded!")).ToList();
    }

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x => x.DeleteDirectory());
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s =>
            {
                var settings = s.SetProjectFile(Solution);

                return settings;
            });
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s =>
            {
                var settings = s
                    .SetProjectFile(Solution)
                    .SetConfiguration(Configuration)
                    .SetVersion(Version)
                    .SetContinuousIntegrationBuild(IsServerBuild)
                    .EnableNoRestore();

                return settings;
            });
        });

    Target RunUnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s =>
            {
                var settings = s
                    .SetProjectFile(Solution)
                    .SetConfiguration(Configuration)
                    .SetProperty("Version", Version)
                    .EnableNoRestore()
                    .EnableNoBuild();

                return settings;
            });
        });

    // The packages are produced by `dotnet pack` straight from the projects, so the dependency
    // groups follow the actual TargetFrameworks instead of a hand-maintained nuspec.
    Target CreatePackage => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            foreach (var project in TargetProjects)
            {
                DotNetPack(s => s
                    .SetProject(project)
                    .SetConfiguration(Configuration)
                    .SetVersion(Version)
                    .SetOutputDirectory(NugetDirectory)
                    .SetContinuousIntegrationBuild(IsServerBuild)
                    .EnableNoRestore()
                    .EnableNoBuild());
            }
        });

    Target PublishPackage => _ => _
        .DependsOn(CreatePackage)
        .DependsOn(RunUnitTests)
        .Executes(() =>
        {
            // In CI this is the short-lived key issued by NuGet trusted publishing (NuGet/login).
            var apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY");

            if (apiKey.IsNullOrEmpty())
            {
                throw new InvalidOperationException("Could not resolve the NuGet API key.");
            }

            // Pushing the .nupkg also uploads the matching .snupkg next to it.
            foreach (var nupkg in NugetDirectory.GlobFiles("*.nupkg"))
            {
                DotNetNuGetPush(s => s
                    .SetTargetPath(nupkg)
                    .SetSource("https://api.nuget.org/v3/index.json")
                    .SetApiKey(apiKey));
            }
        });

    Target PublishPreRelease => _ => _
        .DependsOn(PublishPackage)
        .Executes(() =>
        {
            string gitHubToken;

            if (GitHubActions != null)
            {
                gitHubToken = GitHubActions.Token;
            }
            else
            {
                gitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            }

            if (gitHubToken.IsNullOrEmpty())
            {
                throw new InvalidOperationException("Could not resolve GitHub token.");
            }

            var credentials = new Credentials(gitHubToken);

            GitHubTasks.GitHubClient = new GitHubClient(
                new ProductHeaderValue(nameof(FalloutBuild)),
                new InMemoryCredentialStore(credentials));

            GitHubTasks.GitHubClient.Repository.Release
                .Create("AngleSharp", TargetProjectName, new NewRelease(Version)
                {
                    Name = Version,
                    Body = String.Join(Environment.NewLine, LatestReleaseNotes.Notes),
                    Prerelease = true,
                    TargetCommitish = "devel",
                });
        });

    Target PublishRelease => _ => _
        .DependsOn(PublishPackage)
        .Executes(() =>
        {
            string gitHubToken;

            if (GitHubActions != null)
            {
                gitHubToken = GitHubActions.Token;
            }
            else
            {
                gitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            }

            if (gitHubToken.IsNullOrEmpty())
            {
                throw new InvalidOperationException("Could not resolve GitHub token.");
            }

            var credentials = new Credentials(gitHubToken);

            GitHubTasks.GitHubClient = new GitHubClient(
                new ProductHeaderValue(nameof(FalloutBuild)),
                new InMemoryCredentialStore(credentials));

            GitHubTasks.GitHubClient.Repository.Release
                .Create("AngleSharp", TargetProjectName, new NewRelease(Version)
                {
                    Name = Version,
                    Body = String.Join(Environment.NewLine, LatestReleaseNotes.Notes),
                    Prerelease = false,
                    TargetCommitish = "main",
                });
        });

    Target Package => _ => _
        .DependsOn(RunUnitTests)
        .DependsOn(CreatePackage);

    Target Default => _ => _
        .DependsOn(Package);

    Target Publish => _ => _
        .DependsOn(PublishRelease);

    Target PrePublish => _ => _
        .DependsOn(PublishPreRelease);
}
