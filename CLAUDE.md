# tia-cli

A command-line tool (C#, .NET Framework 4.8) that drives Siemens TIA Portal V20 through Openness.
Before changing anything, read ROADMAP.md (what is planned and decided), CHANGELOG.md (what
exists) and .claude/skills/tia/SKILL.md (how the commands are used).

## Build and test

- `powershell -ExecutionPolicy Bypass -File tools\package.ps1` runs the unit tests and writes
  `dist\tia-cli-<version>-win-x64.zip` and its `.sha256`. It refuses to package when a test fails.
- Every unit test stays green. WiringTests requires every verb in Commands.cs to have a case in
  tools/fieldtest.ps1, and every verb in Help.cs to exist.
- Help.cs is one verbatim string: no double quotes inside it.
- Unit tests cover what runs without TIA. Anything that needs a licensed TIA Portal, PLCSIM or a
  PLC is tested on a machine that has them, by hand or with tools/fieldtest.ps1 (see the
  field-test skill). A change that touches download, upload, online or hardware is not done until
  it has run there.
- A download stops and reloads the CPU: never run one, or write to a PLC, without asking first.

## Versions

- Releases bump the patch number only: 0.4.7, 0.4.8, ... The minor number (0.5.0) is for a big
  new feature, and the maintainer decides when.
- Test builds carry the next release's number with a `-dev.N` suffix: 0.4.8-dev.1, 0.4.8-dev.2.
  Raise N in Directory.Build.props before packaging a new test build, so two builds never share
  a name and `tia --version` tells them apart. Never spend a release number on a test build.
- Each change adds an entry under `## Unreleased` in CHANGELOG.md.

## Releases

1. In CHANGELOG.md, `## Unreleased` becomes `## <version> — <yyyy-mm-dd>`.
2. Directory.Build.props holds `<version>` with no suffix.
3. Commit as `release: <version>`, with a one-line summary in the body.
4. Rebuild the zip from that commit (`tools\package.ps1`), so the binary carries the release commit.
5. Tag `v<version>`, annotated, message `tia-cli <version>`. Push master and the tag.
6. `gh release create v<version> dist\tia-cli-<version>-win-x64.zip dist\tia-cli-<version>-win-x64.zip.sha256 --prerelease --title "tia-cli <version>"`,
   with that version's CHANGELOG section as the notes.

Releases are the tags and their zips; nobody installs from master, so master may hold unreleased
work.

## Branches

- Features go on short-lived branches off master (`feat/<topic>`, `fix/<topic>`) and come back
  through a pull request. Keep them small and merge them soon.
- There is no develop branch.
- Commit only when the maintainer asks.
