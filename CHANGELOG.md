# Changelog

Notable changes to tia-cli. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

### Added

**`project archive` and `project retrieve`.** A project is a folder of several hundred files; the
thing people actually pass around and back up is the single `.zapNN` archive, and making or opening
one meant going through the TIA Portal menus.

- `project archive [<path>]` packs the open project. With no path the archive lands beside the
  project folder under the project's name; a folder gets that name inside it; anything else is the
  file, with the version's extension (`.zap20` for an `.ap20`) added when it is missing. TIA only
  archives a saved project, so unsaved changes are refused by name unless `--save` is given.
  `--minimize` leaves out the data TIA can rebuild. An existing archive is refused unless
  `--overwrite`, and then moved aside until the new one exists rather than deleted first.
- `project retrieve <archive> <dir>` unpacks an archive into a directory and leaves the project
  open, `--upgrade` for one from an older TIA version.
- `project open` on an archive now says to use `project retrieve` instead of failing with a bare
  Openness error.

### Changed

**`install.ps1` approves what it installs.** It used to stay inside your profile and leave the
machine-wide part to `repair.ps1`, which nobody remembers at the moment it matters — so every fresh
install, and every upgrade, left a `tia.exe` TIA Portal had never approved, and the first background
session timed out against an approval window nobody could see. Install now finishes by running
`repair.ps1` elevated: one UAC prompt from a normal shell, none from an admin one. That writes the
Openness whitelist entry for the exact bytes just installed, adds you to the Siemens TIA Openness
group if you are missing, and clears a machine-wide `RUNASADMIN` flag. `-NoElevate` restores the old
profile-only behaviour; declining UAC is reported, not fatal.

**`uninstall.ps1 -All` clears a machine that has collected copies.** It finds every install from
your PATH, the machine PATH, the Openness whitelist, any running `tia.exe` and the default location,
and removes the files, the PATH entries and the registry entries together, asking about each folder
first. Whitelist entries are only cleared wholesale when every install was; keep one, keep its
approval.

**A folder is no longer deleted unless everything in it came from the zip.** Uninstall used to empty
its install folder and then delete the folder — fine for `%LOCALAPPDATA%\Programs\tia-cli`, and a
disaster for someone who installed into `C:\`, a profile root or a Desktop, all of which are
perfectly legal `-InstallDir` values. Those now keep the folder and lose only the files tia put
there, by name, with any leftover DLLs reported rather than assumed.

**The access_denied hint names the fix that works without a dialog.** It used to say to answer TIA
Portal's confirmation window — which is exactly what a background session cannot show, so the advice
was unusable at the moment you needed it. It now says that, and points at `repair.ps1`. The
pending-approval warning does the same.

`repair.ps1` asks for elevation itself instead of refusing to run, takes `-User` and `-AddToGroup`,
approves the `tia.exe` next to it rather than assuming the default install folder, reads a group
holding an orphaned SID without mistaking it for a missing group, and exits non-zero on failure.

## 0.2.0 — 2026-09-16

### Added

**The program-blocks surface, which was two commands and is now the whole lifecycle.** `blocks` and
`block export` could tell you a block existed and hand you its XML; nothing else about the program
was reachable without opening TIA Portal.

- `blocks --tree` draws the folders rather than flattening them, and `--type OB,FB,FC,DB` filters by
  kind. `DB` covers both global and instance data blocks, since that is what people mean by it. The
  tree's spine is ASCII: the console inherits an OEM code page unless somebody has changed it, and
  box-drawing characters arrive there as mojibake.
- `block show` prints a block's header, its full interface — nested struct members and all — and its
  network titles. Openness exposes none of that directly: only `DataBlock` has an `Interface`
  property and it yields nothing but member names, so the block is exported to a temporary file and
  the SimaticML read back. Everything is matched by local element name, because those namespaces
  carry a schema version that moves with each TIA release.
- `block source` writes a block out as SCL, STL or a DB declaration — the readable form, and the one
  that round-trips back in through `source add`. `--deps` brings the types it depends on with it.
  LAD, FBD and GRAPH blocks have no textual form and say so rather than failing obscurely.
- `block import` reads Openness XML back into the project, into `--folder` if you name one, over the
  top of what is there with `--overwrite`.
- `block rename` and `block delete`, and `folder add` / `folder delete` for the tree itself.
  Deleting asks first unless `--force` is given, and refuses outright when stdin is redirected and
  nobody is there to answer.
- `sources`, `source generate` and `source delete` complete the external-source side. `source add`
  is the new name for `scl import`, which still works, and gained `--folder` to put the blocks it
  generates somewhere other than the root.

### Changed

- Block lookup was rewritten. A name that two folders both hold is now refused as ambiguous instead
  of resolving to whichever the old path walk happened to reach, which could silently be the wrong
  block. A folder-qualified name that is not in that folder is likewise a failure — it says where
  the block actually is rather than acting on one somewhere else — while a bare name still finds the
  block wherever it lives. Folder paths may be given with or without the leading `Program blocks`.
- The `group` column in `tia blocks` is called `folder`, matching the new verbs.
- The hints on block failures no longer overwrite a cause that is already understood. Openness
  reports a missing licence as an ordinary `EngineeringException`, so `block source` on an
  unlicensed machine used to answer "only textual blocks have a source form" about a perfectly
  textual block; the licence, security and dead-portal cases now pass through to the error that
  names them, and keep their exit codes.

## 0.1.0 — 2026-09-01

First release. Drives Siemens TIA Portal through the Openness API from a terminal or a script.

### Added

**Sessions.** `tia <command>` joins the TIA Portal already open on your screen, with no setup. Where
that is too slow — opening a project takes minutes and a CLI process lives for a second —
`tia session start` launches a portal and holds it in a background daemon so the next fifty commands
reuse it. A portal that `tia` started is closed with the session; one that was already running is
left alone.

- `portals`, `session start|status|stop`, `shell`

**Projects and hardware.** Create or open a project, add stations from the hardware catalog, set
addresses, and save.

- `project new|open|info|save|close`, `devices`, `catalog`, `device add`, `device ip`
- `device attrs` lists a CPU's Openness attributes and `device set` changes one. Hardware settings
  that have no command of their own are reachable that way, under names that differ between TIA versions.
- `sim create` makes a PLCSIM instance for a device and powers it on, through PLCSIM's own Simulation
  Runtime API - loaded by path and called by reflection, since it ships inside the PLCSIM
  installation. Downloading is then the ordinary `tia download`. S7-1500 and upwards only; PLCSIM's
  API has no S7-1200 type, so those instances are still made by hand. An instance created through the
  API counts as **PLCSIM Advanced** and asks for that licence key - PLCSIM offers a trial - while one
  created by hand in the PLCSIM window does not.
- `download` and `sim start` take `--secret` for the master-secret prompt and `--plc-password` for
  the access-level ones. A CPU that has a master secret - which current firmware needs before it
  compiles - asks for it on every download, so without this a project built entirely from the CLI
  could never be downloaded from it.
- `device protection` sets the access level, the full-access password and the password for
  confidential configuration data. A current CPU refuses to compile until the last two exist, so
  without it every project built from scratch needs a trip through the TIA UI. Passwords given on a
  command line end up in shell history: prefer a prepared project where that matters.

**Program blocks and tags.** Import SCL, compile, list and export blocks, and manage tag tables.

- `blocks`, `block export`, `scl import`, `tables`, `tags`, `table add`, `tag add`, `compile`

**Transfers and editors.**

- `download`, `upload`, `sim start`
- `show hw|device|block|table`, which need a session started with a user interface

**Scripting.** `--json` prints the raw result of any command, and failures map to specific exit
codes: `1` failed, `2` bad arguments, `3` no session, `4` no project, `5` not found, `6` ambiguous,
`7` access denied, `8` licence missing, `9` portal unusable.

**Install and uninstall.** `install.ps1` checks the machine over first — .NET Framework 4.8, an
Openness installation, your membership of the Siemens TIA Openness group — then installs to
`%LOCALAPPDATA%\Programs\tia-cli` and puts it on your PATH. No administrator rights, nothing written
outside your profile. `uninstall.ps1` stops the session before removing anything, so a background
session cannot be orphaned holding a TIA Portal open.

It also clears the two things that stop a downloaded copy from running on a fresh machine, both of
which surface as the daemon refusing to start with "the operation was canceled by the user": the
mark of the web inherited from the downloaded zip, and a `RUNASADMIN` compatibility flag on
`tia.exe`. Where the first Openness approval has not been granted yet, it says how to answer that
dialog — in the foreground, against a portal on screen — rather than leaving a background session to
time out against a window nobody can see.

**repair.ps1.** Diagnoses a machine where `tia` still will not run: environment, mark of the web,
compatibility flags, Openness group membership and token, and the whitelist entry, which it can
write directly from an elevated prompt. `-DryRun` reports without changing anything.

**Build tooling.** `tools/package.ps1` builds the release zip: it runs the tests, publishes fresh
rather than shipping whatever is in `dist\`, and writes a SHA-256 alongside. `tools/build.ps1` is the
development inner loop only and does not touch PATH.

**Tests.** 58 unit tests over the parts that run without Openness — argument parsing, the wire
protocol, error translation and the documented exit-code table, and the daemon state file. For the
rest, `tools/fieldtest.ps1` runs the hardware-dependent commands on a machine that has the hardware
and writes a report to bring back.

### Verified

Exercised against TIA Portal V20 on Windows 10 x64: a full cycle of create project, add a CPU 1214C,
set its address, create tag tables and tags, import SCL, compile clean, list and export blocks, save,
reopen from disk, and confirm everything survived. Install, reinstall over an existing copy, and
uninstall were each run from the built zip.

### Known limitations

**`sim create` makes an instance TIA will not download to.** Verified on V20: the Runtime API creates
an *Advanced* instance (PLCSIM shows it as `EXTERNAL - ADVANCED`), and a download to it fails with
"Connect to module failed" even with TCP/IP communication mode, a routable address, port 102 open and
the software target answered as PLCSIM Advanced. An instance started from TIA's own "Start simulation"
is an *internal softbus* one, and `tia download --interface PLCSIM` puts the program on it first time.
There is no public API for creating the internal kind, and the Advanced kind needs the PLCSIM Advanced
licence. Until that is understood, the working flow is: start the simulation in TIA, then download.

**`sim start` downloads to a simulation but cannot start one.** From TIA V18 the PLCSIM connection
mode is gone; a simulated PLC is a PN/IE target behind the PLCSIM adapter, and TIA will not start an
instance for a download. Power the instance on first, then run `sim start`.

To fix: PLCSIM ships a Runtime API
(`PLCSIM_V<n>\resources\bin\wwwroot\assets\lib\runtime\Siemens.Simatic.Simulation.Runtime.Api.x64.dll`)
that could create and start an instance before the download. The calls, read off the V20 assembly:

```
SimulationRuntimeManager.RegisterInstance(ECPUType, string name) -> IInstance
IInstance.SetIPSuite(uint interfaceIndex, SIPSuite4 suite, bool persistent)
IInstance.PowerOn(uint timeoutMs) -> ERuntimeErrorCode
IInstance.Run(uint timeoutMs)
IInstance.UnregisterInstance()
```

`SIPSuite4` holds `IPAddress`, `SubnetMask`, `DefaultGateway`, each an `SIP`; how an `SIP` is built is
the one piece still unchecked. Load the assembly by path and call it by reflection - the dev machine
has no PLCSIM, so a compile-time reference would break the build. It covers **S7-1500, ET200SP/PRO, software and SINUMERIK CPUs
only**: `ECPUType` has no S7-1200 entry, and `RegisterCustomInstance` rejects the 1214C article number
(`WrongArgument`), though PLCSIM's own UI simulates S7-1200 from firmware 4.0. So an S7-1200
simulation will still have to be started by hand until Siemens exposes it.

### Notes for upgrading

TIA Portal's Openness whitelist records the approved executable's path **and** a hash of its bytes,
so every new version asks for the "Openness access" confirmation once more. Answer that window the
first time you run a new build; until you do, the command waits and appears to hang.

A running session only knows the wire methods of the binary it was started from, so after installing
a version that adds commands, run `tia session stop` and start again — otherwise the old daemon
answers the new verbs with "Unknown method".
