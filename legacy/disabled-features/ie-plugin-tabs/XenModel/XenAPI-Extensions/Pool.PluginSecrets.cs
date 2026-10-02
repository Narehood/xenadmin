/* Copyright (c) Cloud Software Group, Inc.
 *
 * Redistribution and use in source and binary forms,
 * with or without modification, are permitted provided
 * that the following conditions are met:
 *
 * *   Redistributions of source code must retain the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer.
 * *   Redistributions in binary form must reproduce the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer in the documentation and/or other
 *     materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using XenAdmin.Core;

namespace XenAPI
{
    public partial class Pool
    {
        public string GetXCPluginSecret(string plugin_name, IXenObject obj)
        {
            return Get(gui_config, XCPluginSecretName(plugin_name, obj));
        }

        public void RemoveXCPluginSecret(Session session, string plugin_name, IXenObject obj)
        {
            Pool.remove_from_gui_config(session, opaque_ref, XCPluginSecretName(plugin_name, obj));
        }

        public void SetXCPluginSecret(Session session, string plugin_name, IXenObject obj, string secret_uuid)
        {
            string n = XCPluginSecretName(plugin_name, obj);
            Pool.remove_from_gui_config(session, opaque_ref, n);
            Pool.add_to_gui_config(session, opaque_ref, n, secret_uuid);
        }

        private string XCPluginSecretName(string plugin_name, IXenObject obj)
        {
            return string.Format("XC_PLUGIN_SECRET_{0}_{1}_{2}", obj.Connection.Username, plugin_name, Helpers.GetUuid(obj));
        }

    }
}
