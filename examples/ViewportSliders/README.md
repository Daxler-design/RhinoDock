# Viewport Sliders

Source example for rhDock 0.6.10 and Rhino 8 SR30. The Extension uses only public `rhDock.Extensions` APIs and RhinoCommon `DisplayConduit` / `MouseCallback`; its source is packaged as the default example when creating a new Extension Repo. It runs as a reloadable Extension, rather than a built-in host feature.

## Setup

1. Install the 0.6.10 Yak or Release `.rhp`, quit Rhino, and restart. Use only one plug-in registration.
2. In a newly created Extension Repo, copy `ViewportSliders/SampleScript.cs.txt` into your Loaded Repository as `Components/ViewportSliderScript.cs`. For this source checkout, open `examples/ViewportSliderScript` as the Loaded Repository, or copy its `Components/ViewportSliderScript.cs` into an existing Loaded Repository.
3. Select the Script. Type `2, 4, 6` into `Values`. Create three Rhino Point objects and pick them, in order, for `Anchors`.
4. A new Extension Repo already contains ViewportSliders and enables it for this session. For an existing repo, copy this entire `ViewportSliders` folder into it and enable ViewportSliders in the Extensions tab. Do not enable Load at startup until accepted.
5. Turn Auto on. Expand Details for Values and drag a viewport slider. Each value draws a vertical line at its anchor; Run works in Manual mode too.

The example explicitly pairs `Values[i]` with `Anchors[i]`. It handles `Scripts.Reordered` for Values by calling `ReorderInput` on Anchors, preserving picked-object links. The host does not infer matching. Mismatched counts or types hide the controls. Moving picked Point objects updates the anchors through the normal host pick refresh.

Sliders occupy 120 by 24 viewport pixels, project from each supplied 3D anchor, and edit values between 0 and 10. Overlapping controls pick the last drawn one. Only left clicks inside a control without Ctrl/Shift are intercepted; right/middle-button navigation, Ctrl/Shift, Gumballs, and clicks outside remain Rhino's. Escape or leaving the viewport ends a drag, retaining its last committed value. Each move and the final mouse-up write through `SetInputItem`, which uses existing adaptive Auto scheduling. No input is written from a display event.

## Public editor contracts

`Scripts.RegisterEditor(type, edit)` remains the legacy single-value Edit action. The overload `RegisterEditor(type, (name, index) => ...)` handles both a single value (`index == null`) and a list element; apply with `SetInput` or `SetInputItem` respectively.

An inline editor supplies invariant text to a built-in host control:

```csharp
rh.Scripts.RegisterEditor(typeof(DateTime), "text",
    value => value is DateTime date ? date.ToString("O", CultureInfo.InvariantCulture) : "",
    text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
```

Control kinds are `number`, `int`, `text`, `boolean`, and `color`. The same format/parse pair serves single values, list rows, and whole-list text. Parse errors are logged and keep the committed value. Callbacks are never saved and are cleared on retirement; normal controls return when the Extension is disabled. Arbitrary embedded HTML is not part of this contract. Per-item string editing preserves commas and empty strings; bulk list entry still uses commas as separators.

`ActiveItem` and `SetActiveItem(name, index)` synchronize highlighting without selecting document objects. `Changed` remains coalesced on the UI queue; `Reordered` runs synchronously after a move so an Extension can explicitly keep its mapping consistent before the next Auto Run. `HasDocument` and `Revision` let viewport tools reject stale interaction after a document, Script, or source change. Active rows are transient and are not saved.

## Acceptance

Builds and portable checks do not establish live Rhino behavior. Record Rhino version, OS, plug-in revision, expected/actual results, and useful logs for each item. All live items below remain untested.

- [ ] Numeric row edits and viewport drags update the same value and line; mouse-up retains the final value with Auto on and off.
- [ ] Activating a Values row highlights that slider; clicking a slider highlights its panel row without selecting Point objects.
- [ ] Move a value row up/down and drag-reorder it; its anchor follows, Point-object links remain, and the correct line still responds.
- [ ] Move the picked Points; orbit, pan, and zoom. Controls follow their anchors and stay readable.
- [ ] Existing geometry Details still select, replace, add, reorder, show/hide, and clear picked objects and Sub-objects.
- [ ] Numbers, text, booleans, and colors edit per item. Register an indexed Edit action and an inline editor; test single values and list elements, parse errors, disable fallback, and supported Workspace restore.
- [ ] Click outside controls, use Gumballs, navigate with mouse/modifiers, release a drag outside the viewport, and press Escape. Ordinary Rhino interaction continues.
- [ ] Change Script, save/reload its source, close/switch document, reload the Extension repeatedly, then disable it. No stale controls or callbacks remain and no "still loaded" report appears.
- [ ] Repeat on Windows and macOS Rhino 8 SR30 before merging or publishing.

Validation on 2026-10-14: Debug/Release host builds, portable list/editor checks, frontend production build (classic script), shared-row render/action checks, example compilation and Script discovery, plug-in metadata, and Yak packaging pass on macOS. The browser sample demonstrated an item edit from 4 to 7.25 and reordering with its active highlight. Live Rhino validation remains open.
