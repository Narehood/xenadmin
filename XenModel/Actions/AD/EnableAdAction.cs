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
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;


namespace XenAdmin.Actions
{
    public class EnableAdAction : AsyncAction
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string domain;
        private string user;
        private string password;
        private readonly bool requireCleanDisable;

        public EnableAdAction(IXenConnection connection, string domain, string user, string password, bool requireCleanDisable = false)
            : base(connection, string.Format(Messages.ENABLING_AD_ON, Helpers.GetName(connection).Ellipsise(50)), Messages.ENABLING_AD, false)
        {
            if (string.IsNullOrEmpty(domain))
                throw new ArgumentException("domain");
            if (string.IsNullOrEmpty(user))
                throw new ArgumentException("user");
            if (password == null)
                throw new ArgumentNullException("password");

            var pool = Helpers.GetPool(connection);
            if (pool != null)
                Pool = pool;
            else
                Host = Helpers.GetCoordinator(connection);

            this.domain = domain;
            this.user = user;
            this.password = password;
            this.requireCleanDisable = requireCleanDisable;
            ApiMethodsToRoleCheck.Add("pool.disable_external_auth");
            ApiMethodsToRoleCheck.Add("pool.enable_external_auth");
        }

        protected override void Run()
        {
            log.DebugFormat("Enabling AD on pool '{0}'", Helpers.GetName(Connection));

            Dictionary<string, string> config = new Dictionary<string, string>();
            config["domain"] = domain; // NB this line is now redundant, it is here to support the old now-superseded way of passing in the domain
            config["user"] = user;
            config["pass"] = password;
            try
            {
                var pool = Helpers.GetPoolOfOne(Connection);
                try
                {
                    //CA-48122: Call disable just in case it was not disabled properly
                    Pool.disable_external_auth(Session, pool.opaque_ref, new Dictionary<string, string>());
                }
                catch (Exception)
                {
                    if (requireCleanDisable)
                        throw new InvalidOperationException("The preparatory domain leave was not confirmed. Inspect every host before attempting domain join again.");
                    // Server error details can echo the submitted credentials.
                    log.Debug("The best-effort preparatory domain leave was not confirmed.");
                }

                Pool.enable_external_auth(Session, pool.opaque_ref, config, domain, Auth.AUTH_TYPE_AD);
            }
            catch (Failure f) when (f.ErrorDescription.Count > 0 && f.ErrorDescription[0] == Failure.POOL_AUTH_ENABLE_FAILED_WRONG_CREDENTIALS)
            {
                throw new CredentialsFailure(new List<string> { Failure.POOL_AUTH_ENABLE_FAILED_WRONG_CREDENTIALS, "", "Server details omitted to protect credentials." });
            }
            catch (Exception)
            {
                // Never retain an inner exception: directory providers can echo a
                // password in arbitrary error text, which AsyncAction logs.
                throw new InvalidOperationException("Domain join was not confirmed. Check domain credentials, DNS and time synchronization, then inspect every host before another attempt. Server error details were omitted to protect credentials.");
            }
            finally { config.Clear(); user = null; password = null; }
            Description = Messages.COMPLETED;
        }

        protected override void Clean() { user = null; password = null; base.Clean(); }

        /// <summary>
        /// Exception thrown when enabling AD authentication fails due to wrong supplied credentials
        /// </summary>
        public class CredentialsFailure : Failure
        {
            public CredentialsFailure(List<string> err)
                : base(err)
            {
            }
        }
    }
}
