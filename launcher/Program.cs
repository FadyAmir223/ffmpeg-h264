using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"h264-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporary);
            using var bundle = Assembly.GetExecutingAssembly().GetManifestResourceStream("bundle.zip")
                ?? throw new InvalidOperationException("Embedded converter bundle is missing.");
            ZipFile.ExtractToDirectory(bundle, temporary);

            var script = Path.Combine(temporary, "bin", "h264.ps1");
            if (args.Length == 2)
                return RunConverter(script, args[0], args[1]).GetAwaiter().GetResult();

            ApplicationConfiguration.Initialize();
            Application.Run(new ConverterForm(script));
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length != 2)
                MessageBox.Show(error.Message, "H.264 Converter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task<int> RunConverter(
        string script, string input, string output, Action<ConversionUpdate>? report = null)
    {
        var process = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.ArgumentList.Add("-NoProfile");
        process.ArgumentList.Add("-ExecutionPolicy");
        process.ArgumentList.Add("Bypass");
        process.ArgumentList.Add("-File");
        process.ArgumentList.Add(script);
        process.ArgumentList.Add("-InputPath");
        process.ArgumentList.Add(input);
        process.ArgumentList.Add("-OutputPath");
        process.ArgumentList.Add(output);

        using var child = Process.Start(process)
            ?? throw new InvalidOperationException("Could not start the converter.");
        var errors = child.StandardError.ReadToEndAsync();
        while (await child.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("H264GUI:")) continue;
            var update = JsonSerializer.Deserialize<ConversionUpdate>(line[8..]);
            if (update is not null) report?.Invoke(update);
        }
        await child.WaitForExitAsync();
        await errors;
        return child.ExitCode;
    }
}

internal sealed record ConversionUpdate(string Event, int Index, int Total, string Path, int Percent);

internal sealed class ConverterForm : Form
{
    private readonly string _script;
    private readonly TextBox _from = new() { Dock = DockStyle.Fill };
    private readonly TextBox _to = new() { Dock = DockStyle.Fill };
    private readonly Button _submit = new() { Text = "Convert", AutoSize = true };
    private readonly Label _status = new() { Text = "Choose the source and destination folders.", AutoSize = true };
    private readonly Label _currentFile = new() { AutoEllipsis = true, Dock = DockStyle.Fill, Height = 24 };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private bool _running;

    internal ConverterForm(string script)
    {
        _script = script;
        Text = "H.264 Old Receiver Converter";
        ClientSize = new Size(620, 225);
        MinimumSize = new Size(520, 265);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AddFolderRow(layout, 0, "From:", _from);
        AddFolderRow(layout, 1, "To:", _to);
        layout.Controls.Add(_submit, 2, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.SetColumnSpan(_status, 3);
        layout.Controls.Add(_currentFile, 0, 4);
        layout.SetColumnSpan(_currentFile, 3);
        layout.Controls.Add(_progress, 0, 5);
        layout.SetColumnSpan(_progress, 3);
        Controls.Add(layout);

        AcceptButton = _submit;
        _submit.Click += Convert;
        FormClosing += (_, eventArgs) =>
        {
            if (!_running) return;
            eventArgs.Cancel = true;
            MessageBox.Show("Please wait for the conversion to finish.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
    }

    private void AddFolderRow(TableLayoutPanel layout, int row, string label, TextBox textBox)
    {
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = label == "From:" ? "Choose the source folder" : "Choose the destination folder",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(textBox.Text) ? textBox.Text : string.Empty
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                textBox.Text = dialog.SelectedPath;
        };

        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(textBox, 1, row);
        layout.Controls.Add(browse, 2, row);
    }

    private async void Convert(object? sender, EventArgs eventArgs)
    {
        var input = _from.Text.Trim();
        var output = _to.Text.Trim();
        if (!Directory.Exists(input) || !Directory.Exists(output))
        {
            MessageBox.Show("Choose two existing folders.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.Equals(Path.GetFullPath(input).TrimEnd('\\'), Path.GetFullPath(output).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("The From and To folders must be different.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _running = true;
        _submit.Enabled = _from.Enabled = _to.Enabled = false;
        _status.Text = "Finding videos...";
        _currentFile.Text = string.Empty;
        _progress.Value = 0;
        try
        {
            var exitCode = await Program.RunConverter(_script, input, output, ShowProgress);
            _status.Text = exitCode == 0 ? "Conversion finished." : "Conversion finished with errors.";
            MessageBox.Show(_status.Text, Text, MessageBoxButtons.OK,
                exitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error)
        {
            _status.Text = "Conversion failed.";
            MessageBox.Show(error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _running = false;
            _submit.Enabled = _from.Enabled = _to.Enabled = true;
        }
    }

    private void ShowProgress(ConversionUpdate update)
    {
        if (update.Event == "Total")
        {
            _status.Text = update.Total == 0 ? "No videos found." : $"{update.Total} videos found.";
            return;
        }

        _status.Text = $"Video {update.Index} of {update.Total} — {update.Percent}%";
        _currentFile.Text = Path.GetFileName(update.Path) + (update.Event switch
        {
            "Done" => " — Done",
            "Skipped" => " — Skipped",
            "Failed" => " — Failed",
            _ => string.Empty
        });
        _progress.Value = Math.Clamp(update.Percent, 0, 100);
    }
}
