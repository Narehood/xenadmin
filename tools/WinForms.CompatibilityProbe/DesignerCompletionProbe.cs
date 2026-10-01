using System.ComponentModel;
using System.Drawing;

internal sealed partial class LifecycleDesignerProbe
{
    private void CheckCompletedDesignerDefaults()
    {
        CheckDesignerValues("Ballooning.VMShinyBar", ("Increment", 64d * 1024 * 1024));
        CheckDesignerValues("ShadowPanel", ("PanelColor", Color.AliceBlue), ("BorderColor", Color.DarkBlue));
        CheckDesignerValues("CustomGridView.GridView", ("HasLeftExpanders", false));
        CheckDesignerValues("TagButton", ("IsSelected", true));
        CheckDesignerValues("TabControl.CustomTabControl", ("Multiline", true), ("Alignment", TabAlignment.Bottom));
        CheckDesignerValues("CustomDataGraph.DataPlotNav",
            ("GraphWidth", TimeSpan.FromHours(1)), ("GridSpacing", TimeSpan.FromMinutes(15)),
            ("GraphOffset", TimeSpan.FromMinutes(5)));
    }

    private void CheckGraphDesignerReferences()
    {
        using var nav = (Control)Activator.CreateInstance(ClientType("Controls.CustomDataGraph.DataPlotNav"))!;
        using var events = (Control)Activator.CreateInstance(ClientType("Controls.CustomDataGraph.DataEventList"))!;
        using var graphs = (Control)Activator.CreateInstance(ClientType("Controls.CustomDataGraph.GraphList"))!;
        using var restored = (Control)Activator.CreateInstance(graphs.GetType())!;
        var properties = TypeDescriptor.GetProperties(graphs);
        foreach (var (name, value) in new[] { ("DataPlotNav", nav), ("DataEventList", events) })
        {
            var property = properties[name]!;
            Require(property.SerializationVisibility == DesignerSerializationVisibility.Visible,
                $"GraphList.{name}: designer-created component references must remain serializable.");
            property.SetValue(graphs, value);
            property.SetValue(restored, property.GetValue(graphs));
            Require(ReferenceEquals(property.GetValue(restored), value),
                $"GraphList.{name}: a component reference must survive descriptor replay.");
        }
        foreach (var control in new[] { nav, graphs })
            Require(TypeDescriptor.GetProperties(control)["ArchiveMaintainer"]!.SerializationVisibility
                == DesignerSerializationVisibility.Hidden, "Live graph archives must not become designer resources.");

        var resources = new ComponentResourceManager(ClientType("TabPages.PerformancePage"));
        resources.ApplyResources(nav, "DataPlotNav", System.Globalization.CultureInfo.InvariantCulture);
        var uuids = TypeDescriptor.GetProperties(nav)["DisplayedUuids"]!;
        Require(uuids.SerializationVisibility == DesignerSerializationVisibility.Visible,
            "Graph navigator: the checked-in resource-backed UUID list must remain serializable.");
        Require(uuids.GetValue(nav) is System.Collections.IList,
            "Graph navigator: the existing UUID list resource must still load.");
        resources.ReleaseAllResources();
    }

    private void CheckGridEnabledState()
    {
        var type = ClientType("Controls.DataGridViewEx.DataGridViewEx");
        using var parent = new Panel();
        using var grid = (DataGridView)Activator.CreateInstance(type)!;
        parent.Controls.Add(grid);
        var property = TypeDescriptor.GetProperties(grid)["Enabled"]!;
        Require(!property.ShouldSerializeValue(grid), "Grid: its initial local enabled state must be omitted.");
        parent.Enabled = false;
        Require(!grid.Enabled && !property.ShouldSerializeValue(grid),
            "Grid: a disabled parent must not persist a locally disabled child.");
        property.SetValue(grid, false);
        Require(property.ShouldSerializeValue(grid), "Grid: explicitly disabled local state must be serialized.");
        property.ResetValue(grid);
        parent.Enabled = true;
        Require(grid.Enabled && !property.ShouldSerializeValue(grid), "Grid: reset must restore local enabled state.");
        var enabledStyle = type.GetField("EnabledStyle", InstanceMembers)!.GetValue(grid);
        Require(ReferenceEquals(grid.ColumnHeadersDefaultCellStyle, enabledStyle),
            "Grid: reset must run the custom setter to restore its enabled header style.");
    }

    private void CheckCredentialDesignerState()
    {
        using var prompt = (Control)Activator.CreateInstance(ClientType("Dialogs.AdPasswordPrompt"), [true, null])!;
        var properties = TypeDescriptor.GetProperties(prompt);
        foreach (var (name, text) in new[] { ("Domain", "synthetic.example"), ("Username", "synthetic-user") })
        {
            var property = properties[name]!;
            property.SetValue(prompt, text);
            Require(Equals(property.GetValue(prompt), text), "AD prompt: runtime account edits must still work.");
            Require(property.SerializationVisibility == DesignerSerializationVisibility.Hidden,
                $"AD prompt: {name} must not be captured by the designer.");
        }
    }
}
