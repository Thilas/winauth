﻿/*
 * Copyright (C) 2013 Colin Mackie.
 * This software is distributed under the terms of the GNU General Public License.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Xml;

namespace WinAuth
{
    /// <summary>
    /// Class holding the latest version information
    /// </summary>
    public class WinAuthVersionInfo
    {
        /// <summary>
        /// Version number
        /// </summary>
        public Version Version;

        /// <summary>
        /// Date of release
        /// </summary>
        public DateTime Released;

        /// <summary>
        /// URL for download
        /// </summary>
        public string Url;

        /// <summary>
        /// Optional changes
        /// </summary>
        public string Changes;

        /// <summary>
        /// Create the new version instance
        /// </summary>
        /// <param name="version"></param>
        public WinAuthVersionInfo(Version version)
        {
            Version = version;
        }
    }

    /// <summary>
    /// Class to check for newer version of WinAuth
    /// </summary>
    public class WinAuthUpdater
    {
        /// <summary>
        /// Period when the poller thread will check if it needs to check for a new version
        /// </summary>
        protected const int UPDATECHECKTHREAD_SLEEP = 6 * 60 * 60 * 1000; // 6 hrs to check if we need to check

        /// <summary>
        /// Registry key value name for when we last checked for a new version
        /// </summary>
        protected const string WINAUTHREGKEY_LASTCHECK = WinAuthHelper.WINAUTHREGKEY + "\\LastUpdateCheck";

        /// <summary>
        /// Registry key value name for how often we check for a new version
        /// </summary>
        protected const string WINAUTHREGKEY_CHECKFREQUENCY = WinAuthHelper.WINAUTHREGKEY + "\\UpdateCheckFrequency";

        /// <summary>
        /// Registry key value name for the last version we found when we checked
        /// </summary>
        protected const string WINAUTHREGKEY_LATESTVERSION = WinAuthHelper.WINAUTHREGKEY + "\\LatestVersion";

        /// <summary>
        /// The interval for checking new versions. Null is never, Zero is each time, else a period.
        /// </summary>
        private TimeSpan? _autocheckInterval;

        /// <summary>
        /// Current Config
        /// </summary>
        protected WinAuthConfig Config { get; set; }

        /// <summary>
        /// Create the version checker instance
        /// </summary>
        public WinAuthUpdater(WinAuthConfig config)
        {
            Config = config;

            // read the update interval and last known latest version from the registry
            if (TimeSpan.TryParse(Config.ReadSetting(WINAUTHREGKEY_CHECKFREQUENCY, string.Empty), out var interval))
            {
                _autocheckInterval = interval;
            }

            if (long.TryParse(Config.ReadSetting(WINAUTHREGKEY_LASTCHECK, null), out var lastCheck))
            {
                LastCheck = new DateTime(lastCheck);
            }

            if (Version.TryParse(Config.ReadSetting(WINAUTHREGKEY_LATESTVERSION, string.Empty), out var version))
            {
                LastKnownLatestVersion = version;
            }
        }

        #region Properties

        /// <summary>
        /// Get when the last check was done
        /// </summary>
        public DateTime LastCheck { get; private set; }

        /// <summary>
        /// Get the last known latest version or null
        /// </summary>
        public Version LastKnownLatestVersion { get; protected set; }

        /// <summary>
        /// Get the current version
        /// </summary>
        public Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

        /// <summary>
        /// Get flag if we have autochecking enabled
        /// </summary>
        public bool IsAutoCheck => _autocheckInterval != null;

        /// <summary>
        /// Get the interval between checks
        /// </summary>
        public TimeSpan? UpdateInterval => _autocheckInterval;

        #endregion

        /// <summary>
        /// Start an AutoCheck thread that will periodically check for a new version and make a callback
        /// </summary>
        /// <param name="callback">Callback when a new version is found</param>
        public void AutoCheck(Action<Version> callback)
        {
            // create a thread to check for latest version
            var thread = new Thread(new ParameterizedThreadStart(AutoCheckPoller))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start(callback);
        }

        /// <summary>
        /// AutoCheck thread method to poll for new version
        /// </summary>
        /// <param name="p">our callback</param>
        protected virtual void AutoCheckPoller(object p)
        {
            var callback = p as Action<Version>;

            do
            {
                // only if autochecking is on, and is due, and we don't already have a later version
                if (IsAutoCheck
                    && _autocheckInterval.HasValue && LastCheck.Add(_autocheckInterval.Value) < DateTime.Now
                    && (LastKnownLatestVersion == null || LastKnownLatestVersion <= CurrentVersion))
                {
                    // update the last check time
                    LastCheck = DateTime.Now;
                    Config.WriteSetting(WINAUTHREGKEY_LASTCHECK, LastCheck.Ticks.ToString());

                    // check for latest version
                    try
                    {
                        var latest = GetLatestVersion();
                        if (latest != null && latest.Version > CurrentVersion)
                        {
                            callback(latest.Version);
                        }
                    }
                    catch (Exception) { }
                }

                Thread.Sleep(UPDATECHECKTHREAD_SLEEP);

            } while (true);
        }

        /// <summary>
        /// Explicitly get the latest version information. Will be asynchronous if a callback is provided.
        /// </summary>
        /// <param name="callback">optional callback for async operation</param>
        /// <returns>latest WinAuthVersionInfo or null if async</returns>
        public virtual WinAuthVersionInfo GetLatestVersion(Action<WinAuthVersionInfo, bool, Exception> callback = null)
        {
            // get the update URL from the config else use the default
            var updateUrl = WinAuthMain.WINAUTH_UPDATE_URL;
            try
            {
                var settings = new System.Configuration.AppSettingsReader();
                var appvalue = settings.GetValue("UpdateCheckUrl", typeof(string)) as string;
                if (!string.IsNullOrEmpty(appvalue))
                {
                    updateUrl = appvalue;
                }
            }
            catch (Exception) { }
            try
            {
                using (var web = new WebClient())
                {
                    web.Headers.Add("User-Agent", "WinAuth-" + CurrentVersion.ToString());
                    if (callback == null)
                    {
                        // immediate request
                        var result = web.DownloadString(updateUrl);
                        var latestVersion = ParseGetLatestVersion(result);
                        if (latestVersion != null)
                        {
                            // update local values
                            LastKnownLatestVersion = latestVersion.Version;
                            Config.WriteSetting(WINAUTHREGKEY_LATESTVERSION, latestVersion.Version.ToString(3));
                        }
                        return latestVersion;
                    }
                    else
                    {
                        // initiate async operation
                        web.DownloadStringCompleted += new DownloadStringCompletedEventHandler(GetLatestVersionDownloadCompleted);
                        web.DownloadStringAsync(new Uri(updateUrl), callback);
                        return null;
                    }
                }
            }
            catch (Exception)
            {
                // don't fail if we can't get latest version
                return null;
            }
        }

        /// <summary>
        /// Callback for async operation for latest version web request
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void GetLatestVersionDownloadCompleted(object sender, DownloadStringCompletedEventArgs args)
        {
            // no point if e have no callback
            if (!(args.UserState is Action<WinAuthVersionInfo, bool, Exception> callback))
            {
                return;
            }

            // report cancelled or error
            if (args.Cancelled || args.Error != null)
            {
                callback(null, args.Cancelled, args.Error);
                return;
            }

            try
            {
                // extract the latest version
                var latestVersion = ParseGetLatestVersion(args.Result);
                if (latestVersion != null)
                {
                    // update local values
                    LastKnownLatestVersion = latestVersion.Version;
                    Config.WriteSetting(WINAUTHREGKEY_LATESTVERSION, latestVersion.Version.ToString(3));
                }
                // perform callback
                callback(latestVersion, false, null);
            }
            catch (Exception ex)
            {
                // report any other error
                callback(null, false, ex);
            }
        }

        /// <summary>
        /// Parse the returned xml from the website request to extract version information
        /// </summary>
        /// <param name="result">version xml information</param>
        /// <returns>new WinAuthVersionInfo object</returns>
        private WinAuthVersionInfo ParseGetLatestVersion(string result)
        {
            // load xml document and pull out nodes
            var xml = new XmlDocument();
            xml.LoadXml(result);
            var node = xml.SelectSingleNode("//version");

            Version.TryParse(node.InnerText, out var version);
            if (node != null && version != null)
            {
                var latestversion = new WinAuthVersionInfo(version);

                node = xml.SelectSingleNode("//released");
                if (node != null && DateTime.TryParse(node.InnerText, out var released))
                {
                    latestversion.Released = released;
                }
                node = xml.SelectSingleNode("//url");
                if (node != null && !string.IsNullOrEmpty(node.InnerText))
                {
                    latestversion.Url = node.InnerText;
                }
                node = xml.SelectSingleNode("//changes");
                if (node != null && !string.IsNullOrEmpty(node.InnerText))
                {
                    latestversion.Changes = node.InnerText;
                }

                return latestversion;
            }
            else
            {
                throw new InvalidOperationException("Invalid return data");
            }
        }

        /// <summary>
        /// Set the interval for automatic update checks. Null is disabled. Zero is every time.
        /// </summary>
        /// <param name="interval">new interval or null to disable</param>
        public void SetUpdateInterval(TimeSpan? interval)
        {
            // get the next check time
            if (interval != null)
            {
                // write into regisry

                Config.WriteSetting(WINAUTHREGKEY_CHECKFREQUENCY, string.Format("{0:00}.{1:00}:{2:00}:{3:00}", (int)interval.Value.TotalDays, interval.Value.Hours, interval.Value.Minutes, interval.Value.Seconds)); // toString("c") is Net4

                // if last update not set, set to now
                if (Config.ReadSetting(WINAUTHREGKEY_LASTCHECK) == null)
                {
                    Config.WriteSetting(WINAUTHREGKEY_LASTCHECK, DateTime.Now.Ticks.ToString());
                }
            }
            else
            {
                // remove from registry
                Config.WriteSetting(WINAUTHREGKEY_CHECKFREQUENCY, null);
            }
            // update local values
            _autocheckInterval = interval;
        }
    }
}
