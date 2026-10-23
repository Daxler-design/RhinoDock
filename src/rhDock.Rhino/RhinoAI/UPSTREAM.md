# RhinoAI tool implementation

Source: https://github.com/mcneel/RhinoAI/tree/46756c0f09c135e86e0748c8ae21929252402039/rhino/plugin

MIT, Copyright (c) 2026 Robert McNeel & Associates. See LICENSE and the packaged third-party notice.

This is the Rhino 8 external tool set, including Grasshopper 1. Rhino 9-only tools (Grasshopper 2 and manage_plugin_commands) are excluded exactly as in the upstream R8 target. ask_user is an upstream InPanelOnly tool, not an external MCP tool; rhDock retains its native harness question cards. No upstream agent, panel, router, plugin registration, or listener is included.

Adaptations: namespace Rhino.AI becomes rhDock.RhinoAI to coexist with RhinoAI; global usings are file-local and trailing whitespace is normalized; the Rhino 8 script runner is selected directly; the host supplies authorization and transport. Upstream source hashes are in upstream-files.json. Local modifications must be listed here.

The compiled external catalog is 28 Rhino 8 tools plus three external resources. The smoke catalog check compares the compiled assembly against that upstream tool contract. Geometry descriptions from rhDock's existing GeometryVocabulary.Describe augment selection and object-list summaries. ToolHandler accepts an execution-time validation callback, checked on the actual execution thread. SchemaBuilder and ParameterBinder accept raw JsonElement values, including null, for rhDock input editing. These are the additional local changes.

get_viewport_image captures through ViewCapture instead of RhinoView.CaptureToBitmap(Size), whose redraw of a Raytraced (realtime) view returned only the overlays on a blank image; a realtime view is captured with as many passes as it had rendered (at least one), reported as metadata.viewport.realtimePasses. Display mode and camera changes the tool makes are still kept, as upstream. Its size cap is 4096 pixels per side instead of 1280 x 720, so a user's request for a larger image can be met; the default is 960 x 540 instead of 480 x 270, and the parameter descriptions keep the agent's own inspection at most 1568 pixels on the longer side (models scale larger images down) and larger sizes, up to 4096, for user requests. It also takes format (jpg, the default, or png) and transparent (PNG only, through ViewCapture.TransparentBackground), so line work and images the user keeps are not blurred by JPEG. After changing the view it calls RhinoApp.Wait() before capturing: with ViewCapture, a display mode set by the same call was otherwise captured one call late.

Host dispatch focuses the bound Rhino document on an earlier UI callback before invoking command/script/close tools. Live macOS testing reproduced Undo being consumed while the Grasshopper window had focus; returning focus to the model made the same command undo the Host Bake.
