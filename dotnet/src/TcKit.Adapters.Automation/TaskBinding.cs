using System.Xml;
using System.Xml.Linq;

namespace TcKit.Adapters.Automation;

/// <summary>
/// File-side scan for PLC task bindings, used to refuse deleting a PROGRAM that a task still calls.
/// Reads the .TcTTO task-object files under the solution directory (the binding lives in
/// &lt;PouCall&gt;&lt;Name&gt;), independent of COM. Self-contained so the automation adapter does not
/// import the reader adapter (adapter isolation).
/// </summary>
internal static class TaskBinding
{
    /// <summary>The first task that calls <paramref name="pouName"/>, or null if none does.</summary>
    public static (string Task, string File)? Find(string solutionDir, string pouName)
    {
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(solutionDir, "*.TcTTO", SearchOption.AllDirectories))
        {
            XElement? task;
            try
            {
                task = XDocument.Load(file).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Task");
            }
            catch (XmlException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            if (task is null)
            {
                continue;
            }

            foreach (var pouCall in task.Elements().Where(e => e.Name.LocalName == "PouCall"))
            {
                var name = pouCall.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value.Trim();
                if (name == pouName)
                {
                    return (task.Attribute("Name")?.Value ?? "", file);
                }
            }
        }

        return null;
    }
}
