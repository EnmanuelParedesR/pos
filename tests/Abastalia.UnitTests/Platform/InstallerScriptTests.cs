using Shouldly;

namespace Abastalia.UnitTests.Platform;

/// <summary>TK-160 (clean VM test): two mistakes in deploy/installer/abastalia.iss that only ISCC or a silent install found.</summary>
public sealed class InstallerScriptTests
{
    // The section header, at the start of a line (a comment in [Run] also names "[Code]": splitting on it alone tested [Run] instead).
    private static readonly string Code = File.ReadAllText(Repo(@"deploy\installer\abastalia.iss")).ReplaceLineEndings("\n").Split("\n[Code]\n")[1];

    [Fact]
    public void A_brace_comment_has_no_brace_inside()
    {
        // Pascal ends a { comment } at the first }: "{app}" inside one broke the compile.
        for (int i = 0, inString = 0; i < Code.Length; i++)
        {
            if (Code[i] == '\'') inString ^= 1;
            if (inString == 1 || Code[i] != '{') continue;
            var end = Code.IndexOf('}', i);
            Code[(i + 1)..end].ShouldNotContain("{", customMessage: $"comment at {i}: {Code[i..end]}");
            i = end;
        }
    }

    [Fact]
    public void Setup_never_waits_on_a_message_box_nobody_sees()
    {
        // /SUPPRESSMSGBOXES (the SYSTEM update task) only answers SuppressibleMsgBox: a plain MsgBox hangs a silent install forever.
        // The uninstall part asks only when not silent.
        var setup = Code.Split("function RemoveData")[0];
        setup.Replace("SuppressibleMsgBox(", "").ShouldNotContain("MsgBox(");
    }

    [Fact]
    public void Sql_express_has_no_telemetry_and_its_download_cannot_hang()
    {
        // TK-291: the telemetry service off; the SSEI download and the extraction run with a time limit and one retry (a bare Exec waits forever).
        Code.ShouldContain("/SQLTELSVCSTARTUPTYPE=Disabled");
        Code.ShouldContain("RunSqlStep(ExpandConstant('{tmp}\\SQLServerExpressSetup.exe')");
        Code.ShouldContain("RunSqlStep(Media + '\\SQLEXPR_x64_ENU.exe'");
        Code.ShouldNotContain("Exec(ExpandConstant('{tmp}\\SQLServerExpressSetup.exe')");
        Code.ShouldNotContain("Exec(Media + '\\SQLEXPR_x64_ENU.exe'");
        // TK-305: the install itself has a time limit, and the usage and error reports are off under the instance's key on every install.
        Code.ShouldContain("ExecWithTimeout(Media + '\\setup\\setup.exe'");
        Code.ShouldNotContain(" Exec(Media + '\\setup\\setup.exe'");
        Code.ShouldContain("RegWriteDWordValue(HKLM, Key, 'CustomerFeedback', 0)");
        Code.ShouldContain("RegWriteDWordValue(HKLM, Key, 'EnableErrorReporting', 0)");
        Code.Split("procedure CurStepChanged")[1].ShouldContain("SqlTelemetryOff;");
    }

    [Fact, Trait("Category", "Windows")]
    public void TK305_remove_data_deletes_the_database_named_in_the_installed_connection_string()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only: runs the script's lines with powershell.exe (the Windows CI job runs it).");
        // The whole script needs an administrator; its lines that pick the server and the database run alone, on a temporary server folder.
        var script = File.ReadAllText(Repo(@"deploy\installer\remove-data.ps1")).ReplaceLineEndings("\n");
        var start = script.IndexOf("# Same order as the service", StringComparison.Ordinal);
        var lines = script[start..script.IndexOf("\nif (-not $NoFinalBackup)", StringComparison.Ordinal)];
        var dir = Directory.CreateTempSubdirectory("abastalia-remove-").FullName;
        try
        {
            string Resolve()
            {
                File.WriteAllText(Path.Combine(dir, "lines.ps1"), $"$ServerDir = '{dir}'; $Server = ''; $Database = ''; $data = 'C:\\nowhere'\n{lines}\n\"$Server|$Database\"");
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
                {
                    ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(dir, "lines.ps1") }, RedirectStandardOutput = true,
                })!;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return output;
            }

            Resolve().ShouldBe(@".\ABASTALIA|Abastalia"); // nothing installed: the setup's defaults
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"Database":{"ConnectionString":"Server=.\\ABASTALIA;Database=Abastalia;Trusted_Connection=True"}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.Production.json"), """{"Database":{"ConnectionString":"Server=PC1\\ABASTALIA;Database=TiendaCentro;Trusted_Connection=True"}}""");
            Resolve().ShouldBe(@"PC1\ABASTALIA|TiendaCentro");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Repo(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Abastalia.slnx not found"), relative.Replace('\\', Path.DirectorySeparatorChar)); // the CI runs these on Linux too
    }
}
