using System.Drawing;
using System.Reflection;
using System.ComponentModel;

internal static class ConnectionLayoutProbe
{
    public static int Run(Assembly assembly)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Check(assembly); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Connection settings layout failed.", failure);
        return 0;
    }

    private static void Check(Assembly assembly)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        var pageType = assembly.GetType("XenAdmin.Dialogs.OptionsPages.ConnectionOptionsPage", throwOnError: true)!;
        // The real options dialog uses a 534px page and a 486px scroll minimum.
        foreach (var width in new[] { 486, 534, 590 })
        {
            using var form = new Form
            {
                ClientSize = new Size(width, 466), StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000), ShowInTaskbar = false, AutoScroll = true
            };
            // Build() loads settings; only construct and lay out controls here.
            using var page = (UserControl)Activator.CreateInstance(pageType)!;
            var resources = new ComponentResourceManager(assembly.GetType("XenAdmin.Dialogs.OptionsDialog", throwOnError: true)!);
            resources.ApplyResources(page, "connectionOptionsPage1");
            form.Controls.Add(page);
            form.Show();
            Application.DoEvents();
            var label = (Label)page.Controls.Find("ProxyAuthenticationScopeLabel", true).Single();
            var preferred = label.GetPreferredSize(new Size(label.Width, 0));
            if (label.Height < preferred.Height || label.Width > label.Parent!.ClientSize.Width)
                throw new InvalidOperationException($"Proxy explanation is clipped at page width {width}: {label.Size} needs {preferred}.");
            for (var ancestor = label.Parent; ancestor != null && ancestor != form; ancestor = ancestor.Parent)
            {
                var relative = ancestor.PointToClient(label.PointToScreen(Point.Empty));
                if (!ancestor.ClientRectangle.Contains(new Rectangle(relative, label.Size)))
                    throw new InvalidOperationException($"Proxy explanation is clipped by {ancestor.Name} at width {width}.");
            }
            var position = page.PointToClient(label.PointToScreen(Point.Empty));
            if (position.Y + label.Height > page.Height || position.X + label.Width > page.Width)
                throw new InvalidOperationException($"Proxy explanation lies outside the page at width {width}.");
            using var bitmap = new Bitmap(page.Width, page.Height);
            page.DrawToBitmap(bitmap, page.ClientRectangle);
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"connection-settings-{width}.png"));
            Console.WriteLine($"Connection settings width={width}: proxy explanation {label.Width}x{label.Height}, preferred height={preferred.Height}, page height={page.Height}.");
            form.Close();
        }
    }
}
