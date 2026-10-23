using Rhino;
using rhDock.RhinoAI.Tools;
namespace rhDock.RhinoAI.ScriptProjects;
// Rhino 8 uses the upstream ScriptEditor runner; Rhino 9 command projects are not part of this target.
internal enum Lang { Python3, CSharp }
internal interface IRhinoCodeRunner { IToolResult RunScript(RhinoDoc doc, Lang lang, string script); }
internal static class ScriptProjectRunner
{
    public static IToolResult RunScript(RhinoDoc doc, Lang lang, string script) => new RhinoAppRunScript().RunScript(doc, lang, script);
}
