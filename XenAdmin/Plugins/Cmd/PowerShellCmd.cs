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

using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;

using XenAPI;
using XenAdmin.Core;

namespace XenAdmin.Plugins
{
    internal class PowerShellCmd : ShellCmd
    {
        /// <summary>
        /// (Optional) "debug" attribute on the "PowerShellCmd" tag
        /// </summary>
        public readonly bool Debug;

        /// <summary>
        /// Optional command/function name from the PowerShellCmd tag; arguments belong in ParamArray.
        /// </summary>
        public readonly string Function;

        public const string ATT_DEBUG = "debug";
        public const string ATT_FUNCTION = "function";

        public PowerShellCmd(XmlNode node, List<string> extraParams)
            : base(node, extraParams)
        {
            Debug = Helpers.GetBoolXmlAttribute(node, ATT_DEBUG);
            Function = Helpers.GetStringXmlAttribute(node, ATT_FUNCTION);
        }

        /// <summary>Creates an unstarted PowerShell plugin process whose literal arguments are staged outside the process command line.</summary>
        /// <param name="procParams">Ordered URL/session/class/UUID tuples, four strings per target.</param>
        /// <param name="targets">Objects used for placeholder substitution; null is supported for the console node.</param>
        /// <returns>An unstarted process; the caller must dispose it even if launch fails.</returns>
        public override Process CreateProcess(List<string> procParams, IList<IXenObject> targets)
        {
            Registry.AssertPowerShellInstalled();

            // Preserve the legacy ObjInfoArray/ParamArray scope while passing all
            // substituted data through the shared literal serializer and payload transport.
            string command = string.Format(
                                         "& {{ {0} }}",
                                         XenServerPowershellCmd.MakeInvocationExpression(Filename, Function, procParams, Params, targets, Debug));

            return XenServerPowershellCmd.CreateCommandProcess(command, Window);
        }
    }
}
