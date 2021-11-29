using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MinVer.Lib
{
    public static class Versioner
    {
        public static Version GetVersion(string workDir, string tagPrefix, MajorMinor minMajorMinor, string buildMeta, VersionPart autoIncrement, string defaultPreReleasePhase, ILogger log)
        {
            log = log ?? throw new ArgumentNullException(nameof(log));

            defaultPreReleasePhase = string.IsNullOrEmpty(defaultPreReleasePhase)
                ? "alpha"
                : defaultPreReleasePhase;

            var version = GetVersion(workDir, tagPrefix, autoIncrement, defaultPreReleasePhase, log).AddBuildMetadata(buildMeta);

            var calculatedVersion = version.Satisfying(minMajorMinor, defaultPreReleasePhase);

            if (calculatedVersion != version)
            {
                log.Info($"Bumping version to {calculatedVersion} to satisfy minimum major minor {minMajorMinor}.");
            }
            else
            {
                log.Debug($"The calculated version {calculatedVersion} satisfies the minimum major minor {minMajorMinor}.");
            }

            log.Info($"Calculated version {calculatedVersion}.");

            return calculatedVersion;
        }

        private static Version GetVersion(string workDir, string tagPrefix, VersionPart autoIncrement, string defaultPreReleasePhase, ILogger log)
        {
            if (!Git.IsWorkingDirectory(workDir, log))
            {
                var version = new Version(defaultPreReleasePhase);

                log.Warn(1001, $"'{workDir}' is not a valid Git working directory. Using default version {version}.");

                return version;
            }

            if (!Git.TryGetHead(workDir, out var head, log))
            {
                var version = new Version(defaultPreReleasePhase);

                log.Info($"No commits found. Using default version {version}.");

                return version;
            }

            var tags = Git.GetTags(workDir, log);

            return GetVersion(head, tags, tagPrefix, autoIncrement, defaultPreReleasePhase, log);
        }

        private static Version GetVersion(Commit head, IEnumerable<Tag> tags, string tagPrefix, VersionPart autoIncrement, string defaultPreReleasePhase, ILogger log)
        {
            var tagsAndVersions = GetTagsAndVersions(tags, tagPrefix, log);

            var commitsChecked = new HashSet<string>();
            var count = 0;
            var height = 0;
            var candidates = new List<Candidate>();
            var commitsToCheck = new Stack<(Commit, int, Commit)>();
            var commit = head;
            Commit? previousCommit = null;

            if (log.IsTraceEnabled)
            {
                log.Trace($"Starting at commit {commit.ShortSha} (height {height})...");
            }

            while (true)
            {
                if (commitsChecked.Add(commit.Sha))
                {
                    ++count;

                    var commitTagsAndVersions = tagsAndVersions.Where(tagAndVersion => tagAndVersion.Tag.Sha == commit.Sha).ToList();

                    if (commitTagsAndVersions.Any())
                    {
                        foreach (var (tag, commitVersion) in commitTagsAndVersions)
                        {
                            var candidate = new Candidate(commit, height, tag.Name, commitVersion, candidates.Count);

                            if (log.IsTraceEnabled)
                            {
                                log.Trace($"Found version tag {candidate}.");
                            }

                            candidates.Add(candidate);
                        }
                    }
                    else
                    {
                        if (log.IsTraceEnabled && commit.Parents.Count > 1)
                        {
                            log.Trace($"History diverges from {commit.ShortSha} (height {height}) to:");

                            foreach (var parent in commit.Parents)
                            {
                                log.Trace($"- {parent.ShortSha} (height {height + 1})");
                            }
                        }

                        foreach (var parent in ((IEnumerable<Commit>)commit.Parents).Reverse())
                        {
                            commitsToCheck.Push((parent, height + 1, commit));
                        }

                        if (commitsToCheck.Count == 0 || commitsToCheck.Peek().Item2 <= height)
                        {
                            var candidate = new Candidate(commit, height, "", new Version(defaultPreReleasePhase), candidates.Count);

                            if (log.IsTraceEnabled)
                            {
                                log.Trace($"Found root commit {candidate}.");
                            }

                            candidates.Add(candidate);
                        }
                    }
                }
                else
                {
                    if (log.IsTraceEnabled)
                    {
                        // previousCommit will always be non-null here
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                        var previousCommitSha = previousCommit.ShortSha;
#pragma warning restore CS8602 // Dereference of a possibly null reference.

                        log.Trace($"History converges from {previousCommitSha} (height {height - 1}) back to previously seen commit {commit.ShortSha} (height {height}). Abandoning path.");
                    }
                }

                if (commitsToCheck.Count == 0)
                {
                    break;
                }

                if (log.IsTraceEnabled)
                {
                    previousCommit = commit;
                }

                var oldHeight = height;
                Commit child;
                (commit, height, child) = commitsToCheck.Pop();

                if (log.IsTraceEnabled)
                {
                    if (child.Parents.Count > 1)
                    {
                        log.Trace($"Following path from {child.ShortSha} (height {height - 1}) through first parent {commit.ShortSha} (height {height})...");
                    }
                    else if (height <= oldHeight)
                    {
                        if (commitsToCheck.Any() && commitsToCheck.Peek().Item2 == height)
                        {
                            log.Trace($"Backtracking to {child.ShortSha} (height {height - 1}) and following path through next parent {commit.ShortSha} (height {height})...");
                        }
                        else
                        {
                            log.Trace($"Backtracking to {child.ShortSha} (height {height - 1}) and following path through last parent {commit.ShortSha} (height {height})...");
                        }
                    }
                }
            }

            log.Debug($"{count:N0} commits checked.");

            var orderedCandidates = candidates.OrderBy(candidate => candidate.Version).ThenByDescending(candidate => candidate.Index).ToList();

            var tagWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Tag?.Length ?? 2) : 0;
            var versionWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Version.ToString().Length) : 0;
            var heightWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Height).ToString(CultureInfo.CurrentCulture).Length : 0;

            if (log.IsDebugEnabled)
            {
                foreach (var candidate in orderedCandidates.Take(orderedCandidates.Count - 1))
                {
                    log.Debug($"Ignoring {candidate.ToString(tagWidth, versionWidth, heightWidth)}.");
                }
            }

            var selectedCandidate = orderedCandidates.Last();

            if (string.IsNullOrEmpty(selectedCandidate.Tag))
            {
                log.Info($"No commit found with a valid SemVer 2.0 version{(string.IsNullOrEmpty(tagPrefix) ? "" : $" prefixed with '{tagPrefix}'")}. Using default version {selectedCandidate.Version}.");
            }

            log.Info($"Using{(log.IsDebugEnabled && orderedCandidates.Count > 1 ? "    " : " ")}{selectedCandidate.ToString(tagWidth, versionWidth, heightWidth)}.");

            return selectedCandidate.Version.WithHeight(selectedCandidate.Height, autoIncrement, defaultPreReleasePhase);
        }

        private static List<(Tag Tag, Version Version)> GetTagsAndVersions(IEnumerable<Tag> tags, string tagPrefix, ILogger log)
        {
            var tagsAndVersions = new List<(Tag Tag, Version Version)>();

            foreach (var tag in tags)
            {
                if (Version.TryParse(tag.Name, out var version, tagPrefix))
                {
                    tagsAndVersions.Add((tag, version));
                }
                else if (log.IsDebugEnabled)
                {
                    log.Debug($"Ignoring non-version tag {tag}.");
                }
            }

            return tagsAndVersions
                .OrderBy(tagAndVersion => tagAndVersion.Version)
                .ThenBy(tagsAndVersion => tagsAndVersion.Tag.Name)
                .ToList();
        }

        private class Candidate
        {
            public Candidate(Commit commit, int height, string tag, Version version, int index)
            {
                this.Commit = commit;
                this.Height = height;
                this.Tag = tag;
                this.Version = version;
                this.Index = index;
            }

            public Commit Commit { get; }

            public int Height { get; }

            public string Tag { get; }

            public Version Version { get; }

            public int Index { get; }

            public override string ToString() => this.ToString(0, 0, 0);

            public string ToString(int tagWidth, int versionWidth, int heightWidth) =>
                $"{{ {nameof(this.Commit)}: {this.Commit.ShortSha}, {nameof(this.Tag)}: {$"'{this.Tag}',".PadRight(tagWidth + 3)} {nameof(this.Version)}: {$"{this.Version},".PadRight(versionWidth + 1)} {nameof(this.Height)}: {this.Height.ToString(CultureInfo.CurrentCulture).PadLeft(heightWidth)} }}";
        }
    }
}
