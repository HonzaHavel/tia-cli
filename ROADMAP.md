# Roadmap

What is planned, in order, with what has been decided so far. Not a promise of dates.

## Address very soon: syncing must not break offline/online consistency

Parked on purpose (2026-10-20), but it decides whether `tia sync` can be used on a project that
is running on a PLC, so it comes right after the current work.

A rebuilt project, or a block regenerated from source, can stop matching the PLC: new interface
timestamps make instance DBs need reinitialising (actual values lost on download), and blocks
that do not match cannot be watched online or snapshotted. VCI shows this today.

- **Consistency probe, no PLC needed:** record an FB's and its instance DB's interface and code
  timestamps, change only the FB body in its `.scl`, sync, read them again, and compare with a copy
  taken before (`PlcSoftware.CompareTo`). Same for a DB where only a start value changed. If the
  interface timestamp survives, syncing only what changed is safe.
- **`tia compare`:** `PlcSoftware.CompareToOnline()` (offline vs PLC), `PlcSoftware.CompareTo` and
  `Device.CompareTo` (project vs project, offline). Read-only. `tia online <station>` exists now
  for the PLC side, if `CompareToOnline` turns out to need an open connection.
- `tia download` already refuses the `DataBlockReinitialization` prompt unless `--force`.
- Actual values: Openness can only export a snapshot taken in TIA (`InterfaceSnapshot`), not take
  one. The plan is to avoid mismatches rather than rescue values after them.
- Principle: the downloaded project (`.tia/archive/`, `.tia/project/`) carries TIA's identities; `source/`
  is its reviewable view. Changes are applied in place; rebuilding is for CI and recovery.

## Parked (2026-10-26): logging in when going online to a protected PLC

Tested on the work machine against PLCSIM Advanced (S7-1500, user management, TIA V20).

**Works:**
- `tia download` / `upload` answer the PLC's certificate and login themselves (`--plc-user`,
  `--plc-password`, `--plc-user-type`, `--accept-certificate`, `--trust-certificate`) — headless too.
  TIA asks these on the transfer's `ConnectionConfiguration.OnlineLegitimation`, as
  `TlsVerificationConfiguration` and `OnlineAuthenticationConfiguration` (TIA's "Local user" is
  `UserType.ProjectUser`). Before this fix download never listened, and every download to a PLC with a
  certificate failed with nothing but "Connect to module failed".
- `tia online` / `offline` in a portal with a window. The certificate goes through tia
  (`--force`/`--accept-certificate` = TIA's Connect button, which is `NonVerified`); the login is
  typed into TIA's own window by the user, once per portal.
- Headless, `tia online` refuses a CPU the project marks as protected (access control on, or a
  restricted access level) instead of letting TIA die trying to show its login window.

**Does not work yet, and why:**
- **Login for `GoOnline()`.** V20 never puts it to Openness: not on the online connection (the
  certificate does arrive there), not on the download's connection (TIA refuses a listener there
  outside a download), not as a portal Confirmation or Notification, not on an MTA thread. TIA shows
  its "Authorized connection" window; headless it crashes the portal. Openness gap, not a tia bug.
- **Filling that window by UI Automation** (`LoginWindowFiller`). Works against a stand-in dialog.
  Against TIA it never matched: the window *is* in the portal's process, but UI Automation names it
  with a resource key, not the visible title — `DialogFrameControl_Siemens.Automation.DomainServices.UI:
  Siemens.Automation.DomainServices.UI.OnlineService.LegitimationDialogResources:
  HeadingLegitimationEnterPasswordHeader` (WinForms, class `WindowsForms10.Window.8.app…`). The filler
  now matches `LegitimationDialog` and falls back to field order (user name, password) when the
  password field is not marked as one — **untested against TIA**. Its log lines (`window seen`,
  `login window fields`) show what it finds.
- **`tia fingerprint`** (`FingerprintDataProvider`, from the portal). Reaches the PLC, then hangs
  headless with no prompt reaching tia — probably the same login, asked where tia cannot answer.

**Next, in order:**
1. Run `tia online --plc-user … --plc-password …` once with the `LegitimationDialog` match and read
   the `login window fields` line: whether the user type, user name and password fields are
   reachable through UI Automation decides whether the filler can work at all.
2. Try V21 Openness (installed on the work machine): `OnlineProvider.GoOnline(ConfigurationAddress)`
   and V21's prompts may route the online login like download's.
3. Ask Siemens how Openness supplies the login for `GoOnline` on V20.
4. Fallback that needs no online login: anonymous access with HMI/diagnostics rights in the PLC's
   user management — a PLC-side choice for the user.
5. Separately: download answers user management with "keep what is on the PLC"; TIA's own default is
   "download all". Decide which tia should follow and offer the other as an option.

## 1. Workspaces — in progress

A folder that holds one TIA project in three forms, found by walking up from the current folder
to `.tia/`, the way git finds `.git`:

```
Line3/
  .tia/
    config     committed: name, TIA version, default station
    project/   not committed: the working project, never stored
    archive/   not committed: Line3.zap20, uploaded to Azure Blob by a separate integration
  source/      committed: the program as files
```

Inside a workspace, commands go to the session or portal that has *this* project open, matched
by its project file - never to whatever portal happens to be running. `tia open` starts TIA
Portal with `.tia/project`, or retrieves `.tia/archive` into it, and exits; commands attach per
call, and only `--headless` needs a session.

To finish the workspace (next feature):

- **Two forms of the same thing, only the human one in git (idea, 2026-10-26).** Tags and hardware
  have the same shape: a form people read and git diffs well (tag CSV, `hardware.yaml`) and the
  form TIA reads (tag XML, CAx AML), with tia converting between them. The workspace could keep
  both - the human form in `source/`, committed; TIA's form generated beside it under `.tia/`, not
  committed, like `.tia/project` - so nothing in git is ever TIA's verbose form, and the TIA form is
  there to import, compare or hand to tools that want it.
- Open with it: repositories that already commit TIA's XML (the team's, and the viewer extension
  that reads them) - whether a `.tia/config` setting picks which form is committed.

## 2. The program as files in `source/`

One folder per station, mirroring TIA's tree:

```
source/PLC_1/
  PLC data types/   *.udt
  PLC tags/         *.csv, written and read by tia through the tag API; TIA's *.xml on request
  Program blocks/   *.scl (SCL), *.awl (STL), *.db (DB), *.xml (LAD, FBD, GRAPH, everything else)
```

`tia export` writes it all in one call, `tia sync` brings back what git says changed and compiles
- both built (2026-10-21), not yet run against a licensed TIA. Still open: whether a new `.udt`
lands in its folder. Found while planning, and to use:

- **Tag tables stay CSV (decided 2026-10-26).** XML was the default for one build and went back:
  Foodtec's 256-tag table is 257 lines as CSV and 25,659 as XML (about 85 of every 100 are empty
  comment slots for 14 languages), and TIA numbers IDs through the file, so one added tag rewrites
  every line after it. Instead the CSV carries what it lacked: a `Value` column for user constants
  and a `Comment <culture>` column per other language - only when a table has them. TIA's XML
  (HonzaHavel's PR #1) stays as `--xml`. A table as both `.csv` and `.xml` in one folder is
  refused; one form deleted with the other beside it is a change of form, not a deletion.

- Every part of the three folders can be exported and imported natively: `PlcBlock.Export`,
  `PlcType.Export` / `PlcTypeComposition.Import`, `PlcTagTable.Export` /
  `PlcTagTableComposition.Import`.
- `PlcBlock.Export` takes `DocumentInfoOptions`; leaving out `CreatedTimeStamp` makes exports
  stable without the text fix in `TiaSession.Blocks.cs` (`WithoutCreatedStamp`).
- `PlcBlock.ExportAsDocuments` / `PlcBlockComposition.ImportFromDocuments` (and the same for
  types): V19+'s document format, which also covers LAD/FBD. Check how readable it is before
  choosing XML for those.

## 3. Hardware as YAML — a main feature, not just for rebuilding

Hardware changes after commissioning too, and the hardware description is what another project
would be generated from. So hardware is described in one short, human-edited YAML per project,
`source/hardware.yaml`, with three directions:

- **export:** project → YAML.
- **apply:** YAML → an *existing* project, changing only what differs (add, remove or move a module,
  change an IP), so unchanged hardware keeps its identity - the same rule as `tia sync`.
- **create:** YAML → an empty project.

Decided (2026-10-20):

- **Short YAML: anything not written is the default.** Defaults depend on order number, firmware
  and TIA version. They are learned by plugging a fresh module of that type into a scratch project,
  and stored per TIA version, so the version in the workspace's `.tia/config` picks the right set.
- **Always written, even when default:** names, order number, firmware, rack and slot, I/O start
  addresses, IP / mask / subnet / router, PROFINET device name - TIA assigns these by plug order or
  name, so omitting them would give different values on a rebuild.
- **IDs:** none in the YAML. Links (node → subnet) are written by name; when AML is generated, IDs
  are derived from each element's path, so the same hardware always gives the same IDs.
- **Generator on top:** a template YAML holds a machine's general structure; a generator fills in
  the rest - IP addresses included, since the networks are local and closed - to produce a
  project's `hardware.yaml`.
- To prove the YAML is enough: export a real project to YAML, build an empty project from it, and
  compare the hardware with the original (`Device.CompareTo`, plus CAx export of both).

Built (2026-10-26): **export**, as `tia hw export <file> --yaml`, drawn from the CAx export
(`HardwareYaml`). It writes the always-written fields above and no settings. Next, in order:

1. Settings: learn each module type's defaults (plug a fresh one into a scratch project, per TIA
   version), then write only what differs - which also brings back what CAx loses (F-capability,
   DCP).
2. **create**: YAML → empty project, then **apply**: YAML → existing project.

How YAML gets into a project - Openness calls (add device, plug modules, set attributes, connect
subnets) or generated AML through `hw import` - is open; Openness calls give per-item errors and
the same defaults as plugging by hand.

### CAx findings so far

`tia hw export` / `tia hw import` exist. `tools/caxtest.sh` finds out on a real project what a
round trip keeps. Known so far (V20, work machine, 2026-10-19):

- Every export changes its file name, a timestamp and every element ID. With those normalized, two
  exports are identical, and TIA imports the normalized file.
- A single S7-1200 station (CPU, onboard I/O, PROFINET address and subnet) survives export → import
  into an empty project → export unchanged, every CPU attribute included. The project name is
  written into the file, so a rebuild has to use the same name.
- **CAx carries the PLC tag tables too**, and skips a tag without an address with an error.

Decided (2026-10-20):

- **One project, one hardware file**, the whole project's hardware, so the subnets between
  stations stay in one place. (Now `hardware.yaml`, above; AML is generated, not committed.)
- **The tag tables are the one source of truth for tags.** The hardware file carries no tags;
  a build creates the hardware first, then imports the tag CSVs.
  Hardware owns modules, address ranges and the network; tags own names, types and addresses.
  To check first: whether I/O channels link to tags by ID, so that a stripped file still imports
  and keeps its channels - a probe step on a project that has I/O tags.
- The protection password (confidential configuration data) is not checked yet: the probe project
  never had one. A rebuild of a protected CPU will at least need `device protection` again.

Run on a real line (2026-10-26, Foodtec: CPU 1515F-2 PN V4.0, ET200MP, 3 × IM 155-6 PN HF V6.2,
ATV320 drives over GSD, 256 I/O tags, access control on, confidential-data password set): export,
import into an empty project of the same name, export again.

- Two exports are identical once normalized; `hw export` now normalizes (IDs from each element's
  path, so a rebuild of the same name gets the same IDs). Every element, channel, tag and link came
  back.
- **I/O tags are linked to their channels by ID** (`InternalLink` channel → tag), and each tag also
  carries its address. Whether a file without tags still imports and the CSV tags reattach by
  address is still to probe.
- **Lost on the way, to be set again after an import:**
  - `Failsafe_FCapabilityActivated` is not in the file at all: an F-CPU with F off came back with
    F on (and with the F-monitoring defaults that come with it).
  - IM 155-6 PN HF V6.2: `DCPReadOnlyActive` is refused on import ("not supported for plugged
    modules with firmware V6.2"), and `DCPSynchronizeActive` came back True instead of False.
  - The confidential-configuration password and with it the PLC communication certificate: the
    rebuild does not compile until `device protection --secret` and a certificate are set again.
- Noise, not hardware: comments gain the new project's language (`en-US` next to `en-GB`), and
  a few defaults are written out once set (server module startup comparison, F-monitoring times).
- So CAx alone is not enough for a rebuild; whatever builds from `hardware.yaml` sets these after
  the import. Next probes: what `--existing overwrite` does to an unchanged station, and whether
  the YAML is better applied by Openness calls than by generated AML.

## 4. Build a project from `source/`

`tia open` with nothing in `.tia/project/` or `.tia/archive/`: create the project, create the hardware from
`source/hardware.yaml`, then data types, tags and blocks, and compile. For CI and recovery - a
rebuilt project should not be downloaded to a running machine without someone deciding to.

## Later: `Siemens.Engineering.VersionControl` — do not forget

Openness V20 has VCI's own API: `VersionControlInterface` (a project service), `Workspace` with a
root folder, `WorkspaceMapping` linking a project object (Program blocks, PLC data types, PLC
tags) to a folder, `MappedObject.Synchronize(ProjectToWorkspace | WorkspaceToProject)`, and a
per-object compare state (`Equal`, `Unequal`, `WorkspaceFileMissing`).

It is very likely what writes the layout the existing TIA repositories already have. If so, it
could replace most of step 2 and make those repositories work with tia unchanged. Probe it on the
work machine once workspaces exist: map a copy of a real project, sync out, compare with an
existing repository, edit, sync back, compile.
