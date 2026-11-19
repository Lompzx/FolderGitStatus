# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.0] - 2026-11-19

### Added
- **Compare with Last Commit** on the context menu of a file: opens the Visual Studio diff between the last commit (`HEAD`) and the working tree, so it shows only what is not committed yet (staged or not), instead of everything the branch changed. It shows up on files with uncommitted changes; new files are compared with an empty file and files renamed but not committed yet with their original name.
- **Compare with Previous Commit** on the context menu of a file: opens the diff of the last commit that changed the file (the file before and after that commit; merge commits are skipped), with the commit message in the header. It follows renames made by that commit or not committed yet, and a file created in the first commit of the repository is compared with an empty file. It shows up on files that are already in a commit.

## [1.4.1] - 2026-11-17

### Fixed
- The **Branch Changes Filter** only worked the first time it was turned on: turning it off and on again showed an empty tree. Turning it off disposed the list of Solution Explorer items that Visual Studio keeps and hands out again the next time the filter is turned on.

## [1.4.0] - 2026-11-17

### Added
- **Show Branch Changes** button on the Solution Explorer toolbar (in its `...` overflow menu when the toolbar is narrow). It turns the option on and off without opening Tools > Options, and its tooltip shows the base branch in use.
- **Branch Changes Filter** in the Solution Explorer filter drop-down: shows only the files changed on the current branch, committed or not. The filtered tree follows new edits, commits and checkouts while the filter is on. It works even with *Show branch changes* off.
- **Compare with Base Branch** on the context menu of a file: opens the Visual Studio diff between the version of the file where the branch left the base branch (merge base) and the working tree. Files renamed on the branch are compared with their original name.
- Per-repository base branch with `git config foldergitstatus.baseBranch <branch>`, which takes precedence over the option (e.g. `develop` in Git Flow repositories).
- **Badge** option: a letter (`M`, `A`, `C`) or, for folders, the number of changed files after the name, so the status does not depend on the color alone.
- **Summary tooltip** option (on by default): hovering a changed folder or file shows how many files are modified, new or in conflict, uncommitted and committed on the branch.
- **Staged changes in italic** option: items whose changes are all staged are shown in italic.
- **Dim ignored items** option: items ignored by Git (`.gitignore`, `.git/info/exclude`) are shown dimmed.
- **Ignored folders** option: the folders whose changes do not trigger a refresh (previously fixed to `bin`, `obj`, `.vs` and `node_modules`, still the default).
- **Git timeout (seconds)** option (default 30): a git command that takes longer is stopped. Before, a git that hung stopped the updates of that repository until Visual Studio was restarted.
- An info bar when git cannot be run (not installed or a wrong path in the options), with a link to the option. It closes by itself once git runs again.
- Unit tests (xUnit) for the parsing of the git output and the status rules, and a GitHub Actions workflow that builds the VSIX, runs the tests and, for `v*` tags, publishes to the Marketplace.

### Changed
- On a selection highlight that would make the status color hard to read (e.g. blue in the Dark and Blue themes), the selected item keeps the theme's text color. Subtle highlights keep the status color.

### Fixed
- In worktrees and submodules (where `.git` is a file), stage-only changes and commits were only shown after another working tree file changed. The actual Git directory is now watched too.
- A repository created (`git init`, clone) inside a solution that was already open was not detected until the solution was reopened.
- Additional *New Solution Explorer View* windows were not colored.

## [1.3.0] - 2026-11-13

### Added
- **Show branch changes** option: also colors the items changed by the commits of the current branch that are not in the base branch (`git diff <base>...HEAD`), in a lighter tone of the same colors. Uncommitted changes keep the full color and take precedence.
- **Base branch** option. It applies to every repository; where it does not exist, or when it is empty, the base is the remote's default branch (`origin/HEAD`), or else `origin/main`, `origin/master`, `main` or `master`. It is looked up again after commits, checkouts and fetches.
- **Committed changes opacity (%)** option (default 55) for the lighter tone.

### Changed
- Changing the options recomputes the status of every repository right away.

## [1.2.2] - 2026-11-13

### Fixed
- The extension could stop coloring Solution Explorer until Visual Studio was restarted (seen on the first launch after updating to Visual Studio 2026 18.12). The tree was looked up only once, so if Visual Studio replaced it with a new one (for example while rebuilding the window layout after an update, or when the window was redocked), the new tree was never colored. The search now starts again whenever the tracked tree leaves the screen, and ends as soon as a tree is shown again.

## [1.2.1] - 2026-10-21

### Fixed
- The extension could stay inactive on the first launch after installing it, until Visual Studio was restarted. The search for the Solution Explorer tree gave up after about 30 seconds, and the "solution opened" event could fire before the package finished loading. The search now keeps going (every 2 s for the first 30 s, then every 10 s) until the tree is found.

### Added
- The Solution Explorer tree is also looked up again when a solution finishes loading in the background and when a folder is opened (*Open Folder* mode).
- `log.txt` keeps previous sessions instead of being cleared on every load. Each session starts with a date header, and the file is moved to `log.old.txt` when it grows past 512 KB.
- The log always records how long after Visual Studio started the package loaded, what triggered the tree search and how long it took to find the tree.

## 1.2.0 - 2026-10-20

### Added
- Extension icon and Marketplace preview image.
- Screenshots in the README.
- `publishManifest.json` with the Marketplace metadata.

### Changed
- Options page, log messages, README and code comments are now in English. The options page is under **Tools > Options > Folder Git Status > General**; previously saved settings are kept.
- Project moved to `src/FolderGitStatus`, with `FolderGitStatus.slnx` at the repository root.

## 1.1.0 - 2026-10-20

First release.

### Added
- Colors folders, projects, solution folders (including `.slnx`) and the solution node in Solution Explorer when they contain Git changes, propagating the state recursively up the tree.
- Colors for modified (red), new (green) and conflicted (amber) files. When a folder has changes of different kinds, the most severe one wins.
- *Colorize files* option, on by default: also colors the names of changed files.
- Automatic refresh on file changes and on stage, commit, checkout and merge, ignoring `bin`, `obj`, `.vs` and `node_modules`.
- Support for multiple repositories and submodules in the same solution, and for *Open Folder* mode.
- Options for the colors, bold text, the path to `git.exe` and a diagnostic log.

[1.5.0]: https://github.com/Lompzx/FolderGitStatus/releases/tag/v1.5.0
[1.4.1]: https://github.com/Lompzx/FolderGitStatus/releases/tag/v1.4.1
[1.4.0]: https://github.com/Lompzx/FolderGitStatus/releases/tag/v1.4.0
[1.3.0]: https://github.com/Lompzx/FolderGitStatus/releases/tag/v1.3.0
[1.2.2]: https://github.com/Lompzx/FolderGitStatus/releases/tag/v1.2.2
[1.2.1]: https://github.com/Lompzx/FolderGitStatus/releases/tag/v1.2.1
