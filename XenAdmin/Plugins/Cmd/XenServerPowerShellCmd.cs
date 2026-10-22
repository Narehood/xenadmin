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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Xml;

using XenAdmin.Core;
using XenAPI;

namespace XenAdmin.Plugins
{
    internal class XenServerPowershellCmd : ShellCmd
    {
        /// <summary>
        /// optional - "debug" attribute on the "XenServerPowerShellCmd" tag
        /// </summary>
        public readonly bool Debug;

        /// <summary>
        /// Optional command/function name from the XenServerPowerShellCmd tag; arguments belong in ParamArray.
        /// </summary>
        public readonly string Function;

        public const string ATT_DEBUG = "debug";
        public const string ATT_FUNCTION = "function";

        private const string XEN_PARAM_ARRAY_VAR_NAME = "ObjInfoArray";
        private const string XEN_PARAM_ARRAY_HASH_KEY_URL = "url";
        private const string XEN_PARAM_ARRAY_HASH_KEY_SESSION_REF = "sessionRef";
        private const string XEN_PARAM_ARRAY_HASH_KEY_CLASS = "class";
        private const string XEN_PARAM_ARRAY_HASH_KEY_OBJECT_UUID = "objUuid";

        private const string USER_PARAM_ARRAY_VAR_NAME = "ParamArray";

        // This is the parameter to a string.Format call, so the double braces come out as single braces in the PowerShell.
        private const string DebugFunction =
            "function __XenCenter_DebugPlugin {{" +
            "  trap [Exception] {{" +
            "    write-host $($_.Exception.GetType().FullName); " +
            "    write-host $($_.Exception.Message);" +
            "    read-host \"[Press Enter to Exit]\" " +
            "  }};" +
            "  {0};" +
            "}};" +
            "__XenCenter_DebugPlugin";

        public XenServerPowershellCmd(XmlNode node, List<string> extraParams)
            : base(node, extraParams)
        {
            Debug = Helpers.GetBoolXmlAttribute(node, ATT_DEBUG);
            Function = Helpers.GetStringXmlAttribute(node, ATT_FUNCTION);
        }

        /// <summary>Creates an unstarted XenServer-module plugin process with a short bootstrap and an owned temporary invocation payload.</summary>
        /// <param name="procParams">Ordered URL/session/class/UUID tuples, four strings per target.</param>
        /// <param name="targets">Objects used for placeholder substitution; null is supported for the console node.</param>
        /// <returns>An unstarted process; the caller must dispose it even if launch fails.</returns>
        public override Process CreateProcess(List<string> procParams, IList<IXenObject> targets)
        {
            Registry.AssertPowerShellInstalled();
            Registry.AssertPowerShellExecutionPolicyNonRestricted();

            string command = MakeInvocationExpression(Filename, Function, procParams, Params, targets, Debug);

            return CreateCommandProcess("Import-Module XenServerPSModule; " + command, Window);
        }

        /// <summary>Stages invocation source in an owned payload; the returned process removes it before execution or on disposal after a failed launch.</summary>
        /// <param name="command">Trusted invocation source containing already serialized literal values.</param>
        /// <param name="window">Whether to display the plugin process window.</param>
        /// <returns>An unstarted process that owns its pending payload; the caller must dispose it.</returns>
        internal static Process CreateCommandProcess(string command, bool window) => new PowerShellProcess(command, window);

        /// <summary>Serializes one nonexpanding PowerShell string by doubling apostrophes and all four Unicode single-quote aliases.</summary>
        /// <param name="value">The untrusted value to serialize as literal data.</param>
        /// <returns>A single-quoted PowerShell expression preserving the original value.</returns>
        internal static string Literal(string value) => "'" + (value ?? string.Empty)
            .Replace("'", "''").Replace("\u2018", "\u2018\u2018").Replace("\u2019", "\u2019\u2019")
            .Replace("\u201A", "\u201A\u201A").Replace("\u201B", "\u201B\u201B") + "'";

        /// <summary>Builds plugin invocation source with literal paths, target metadata and extra values; a function attribute denotes one command name.</summary>
        /// <param name="filename">Plugin script path before placeholder substitution.</param>
        /// <param name="function">Optional single command/function name, with optional placeholders.</param>
        /// <param name="proc_params">Ordered URL/session/class/UUID target tuples.</param>
        /// <param name="extra_params">Extra parameter values before placeholder substitution.</param>
        /// <param name="objs">Targets used when resolving placeholders.</param>
        /// <param name="debug">Whether to wrap execution in the legacy interactive debug trap.</param>
        /// <returns>Trusted invocation source ready for the owned payload transport.</returns>
        public static string MakeInvocationExpression(string filename, string function, IList<string> proc_params,
            IList<string> extra_params, IList<IXenObject> objs, bool debug)
        {
            filename = Placeholders.Substitute(filename, objs);
            var expression = $"Invoke-Expression $(Get-Content -LiteralPath {Literal(filename)} -Raw)";

            if (!string.IsNullOrEmpty(function))
                function = Placeholders.Substitute(function, objs);
            if (!string.IsNullOrEmpty(function))
                expression = $"{expression}; & {Literal(function)}";

            if (debug)
                expression = string.Format(DebugFunction, expression);

            string xenArrayStatement = XenArrayStatement(proc_params);
            string extraArrayStatement = ExtraArrayStatement(extra_params, objs);

            return $"Set-Location -LiteralPath {Literal(Program.AssemblyDir)}; {xenArrayStatement} {extraArrayStatement} {expression};";
        }

        /// <summary>Builds the legacy ObjInfoArray of named hashtables from URL/session/class/UUID tuples without evaluating their values.</summary>
        /// <param name="procParams">Ordered URL/session/class/UUID tuples, four strings per target.</param>
        /// <returns>Source assigning the legacy array of target hashtables.</returns>
        private static string XenArrayStatement(IList<string> procParams)
        {
            // Each target contributes URL, session reference, object class and UUID.
            int count = (int)(procParams.Count / 4);

            // Preserve the legacy array of named hashtables expected by plugin scripts.
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("${0}=@(", XEN_PARAM_ARRAY_VAR_NAME);

            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                int index = i * 4;
                sb.AppendFormat(" @{{{0}={1}; {2}={3}; {4}={5}; {6}={7}}}",
                    XEN_PARAM_ARRAY_HASH_KEY_URL, Literal(procParams[index]),
                    XEN_PARAM_ARRAY_HASH_KEY_SESSION_REF, Literal(procParams[index + 1]),
                    XEN_PARAM_ARRAY_HASH_KEY_CLASS, Literal(procParams[index + 2]),
                    XEN_PARAM_ARRAY_HASH_KEY_OBJECT_UUID, Literal(procParams[index + 3]));
            }
            sb.Append(");");
            return sb.ToString();
        }

        /// <summary>Builds ParamArray from placeholder substitutions serialized as nonexpanding string values.</summary>
        /// <param name="extraParams">Extra parameter values before placeholder substitution.</param>
        /// <param name="objs">Targets used when resolving placeholders.</param>
        /// <returns>Source assigning the extra parameter array.</returns>
        private static string ExtraArrayStatement(IList<string> extraParams, IList<IXenObject> objs)
        {
            // now we form a statement that will initialise a powershell array in the format (a,b,c,d,e,f,g)
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("${0}=@(", USER_PARAM_ARRAY_VAR_NAME));
            for (int i = 0; i < extraParams.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Literal(Placeholders.Substitute(extraParams[i], objs)));
            }
            sb.Append(");");

            return sb.ToString();
        }
    }
}
