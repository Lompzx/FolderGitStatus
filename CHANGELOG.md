# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.2.1]: https://github.com/Lompzx/FolderGitStatus/releases/tag/v1.2.1
