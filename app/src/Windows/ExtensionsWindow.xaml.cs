using System;
using System.IO;
using System.Windows;
using Atypik.i18n;
using Microsoft.Win32;

namespace Atypik.Windows;

public partial class ExtensionsWindow : Window
{
    public ExtensionsWindow()
    {
        InitializeComponent();
        ApplyLocalization();
        LoadInstalledExtensions();
    }

    private void ApplyLocalization()
    {
        Title                  = Loc.T("ext.title");
        ExtSectionLabel.Text   = Loc.T("ext.installed");
        ExtDescription.Text    = Loc.T("ext.empty");
        AddScriptBtn.Content   = Loc.T("ext.add");
        AddModuleBtn.Content   = Loc.T("ext.add_module");
        SecurityLabel.Text     = Loc.T("ext.security");
        SecurityDesc.Text      = Loc.T("ext.security_desc");
    }

    private void LoadInstalledExtensions()
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.*"))
        {
            string ext  = Path.GetExtension(file).ToLower();
            string icon = ext switch
            {
                ".py"   => "🐍",
                ".dll"  => "⚙",
                ".json" => "🔌",
                _       => "📄"
            };
            ExtensionsList.Items.Add($"{icon}  {Path.GetFileName(file)}");
        }

        if (ExtensionsList.Items.Count == 0)
            ExtensionsList.Items.Add(Loc.T("ext.no_extensions"));
    }

    private void OnAddScript(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Loc.T("ext.add_script.title"),
            Filter = Loc.T("ext.add_script.filter"),
        };
        if (dlg.ShowDialog() != true) return;

        CopyToModules(dlg.FileName);
    }

    private void OnAddModule(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Loc.T("ext.add_module.title"),
            Filter = Loc.T("ext.add_module.filter"),
        };
        if (dlg.ShowDialog() != true) return;

        CopyToModules(dlg.FileName);
    }

    private void CopyToModules(string sourcePath)
    {
        string dest = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "modules",
            Path.GetFileName(sourcePath));

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourcePath, dest, overwrite: true);

        ExtensionsList.Items.Clear();
        LoadInstalledExtensions();
    }
}
