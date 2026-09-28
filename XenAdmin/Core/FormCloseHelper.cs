using System;
using System.Windows.Forms;

namespace XenAdmin.Core
{
    internal static class FormCloseHelper
    {
        /// <summary>
        /// Restores the owner's focus after base close processing, unless the owner
        /// or application is shutting down. Must be called on the UI thread.
        /// </summary>
        internal static void RestoreOwnerFocus(Form owner, CloseReason reason, Action<Form> restoreFocus)
        {
            if (reason == CloseReason.ApplicationExitCall || reason == CloseReason.FormOwnerClosing ||
                reason == CloseReason.WindowsShutDown || owner == null || owner.IsDisposed || owner.Disposing)
                return;

            try
            {
                restoreFocus(owner);
            }
            catch (ObjectDisposedException)
            {
                // Focus/activation callbacks can dispose the owner reentrantly.
            }
        }
    }
}
