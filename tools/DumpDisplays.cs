using System;
using System.Collections.Generic;

namespace ChromaPanel
{
    /// <summary>
    /// Console build of the Diagnostics report. Read-only: it never writes a
    /// gamma ramp or touches NVAPI setters, so it is safe to run any time.
    ///
    ///   .\build.ps1 -Tools
    ///   .\build\DumpDisplays.exe
    /// </summary>
    internal static class DumpDisplays
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Console.WriteLine(Report.Build(Displays.Enumerate()));
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex);
            }
        }
    }
}
