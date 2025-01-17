﻿/*
 * Copyright (C) 2015 Colin Mackie.
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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace WinAuth
{
    /// <summary>
    /// Class that implements Steam's SteamGuadrd version of the RFC6238 Authenticator
    /// </summary>
    public class SteamAuthenticator : Authenticator
    {
        /// <summary>
        /// Number of characters in code
        /// </summary>
        private const int CODE_DIGITS = 5;

        /// <summary>
        /// Number of minutes to ignore syncing if network error
        /// </summary>
        private const int SYNC_ERROR_MINUTES = 60;

        /// <summary>
        /// Number of attempts to activate
        /// </summary>
        private const int ENROLL_ACTIVATE_RETRIES = 30;

        /// <summary>
        /// Incorrect activation code
        /// </summary>
        private const int INVALID_ACTIVATION_CODE = 89;

        /// <summary>
        /// Time for http request when calling Sync in ms
        /// </summary>
        private const int SYNC_TIMEOUT = 5000;

        /// <summary>
        /// Steam issuer for KeyUri
        /// </summary>
        private const string STEAM_ISSUER = "Steam";

        /// <summary>
        /// URLs for all mobile services
        /// </summary>
        private static readonly string COMMUNITY_BASE = "https://steamcommunity.com";
        private static readonly string WEBAPI_BASE = "https://api.steampowered.com";
        private static readonly string SYNC_URL = "https://api.steampowered.com:443/ITwoFactorService/QueryTime/v0001";

        /// <summary>
        /// Character set for authenticator code
        /// </summary>
        private static readonly char[] STEAMCHARS = new char[] {
                '2', '3', '4', '5', '6', '7', '8', '9', 'B', 'C',
                'D', 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'P', 'Q',
                'R', 'T', 'V', 'W', 'X', 'Y'};

        /// <summary>
        /// Create logger
        /// </summary>
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Enrolling state
        /// </summary>
        public class EnrollState
        {
            public string Username { get; set; }
            public string Password { get; set; }
            public string CaptchaId { get; set; }
            public string CaptchaUrl { get; set; }
            public string CaptchaText { get; set; }
            public string EmailDomain { get; set; }
            public string EmailAuthText { get; set; }
            public string ActivationCode { get; set; }
            public CookieContainer Cookies { get; set; }

            public string SteamId { get; set; }
            public string OAuthToken { get; set; }

            public bool RequiresLogin { get; set; }
            public bool RequiresCaptcha { get; set; }
            public bool Requires2FA { get; set; }
            public bool RequiresEmailAuth { get; set; }
            public bool RequiresActivation { get; set; }

            public string RevocationCode { get; set; }
            public string SecretKey { get; set; }
            public bool Success { get; set; }

            public string Error { get; set; }
        }

        #region Authenticator data

        /// <summary>
        /// Time of last Sync error
        /// </summary>
        private static DateTime _lastSyncError = DateTime.MinValue;

        /// <summary>
        /// Returned serial number of authenticator
        /// </summary>
        public string Serial { get; set; }

        /// <summary>
        /// Random device ID we created and registered
        /// </summary>
        public string DeviceId { get; set; }

        /// <summary>
        /// JSON steam data
        /// </summary>
        public string SteamData { get; set; }

        /// <summary>
        /// JSON session data
        /// </summary>
        public string SessionData { get; set; }

        /// <summary>
        /// Poll data
        /// </summary>
        public string PollData { get; set; }

        /// <summary>
        /// Current Steam client instance
        /// </summary>
        public SteamClient Client { get; protected set; }

        #endregion

        /// <summary>
        /// Create a new Authenticator object
        /// </summary>
        public SteamAuthenticator()
            : base(CODE_DIGITS)
        {
            Issuer = STEAM_ISSUER;
        }

        /// <summary>
        /// Get/set the combined secret data value
        /// </summary>
        public override string SecretData
        {
            get
            {
                if (Client != null && Client.Session != null)
                {
                    SessionData = Client.Session.ToString();
                }

                //if (Logger != null)
                //{
                //    Logger.Debug("Get Steam data: {0}, Session:{1}", (SteamData ?? string.Empty).Replace("\n"," ").Replace("\r",""), (SessionData ?? string.Empty).Replace("\n", " ").Replace("\r", ""));
                //}

                // this is the key |  serial | deviceid
                return base.SecretData
                    + "|" + ByteArrayToString(Encoding.UTF8.GetBytes(Serial))
                    + "|" + ByteArrayToString(Encoding.UTF8.GetBytes(DeviceId))
                    + "|" + ByteArrayToString(Encoding.UTF8.GetBytes(SteamData))
                    + "|" + (string.IsNullOrEmpty(SessionData) ? string.Empty : ByteArrayToString(Encoding.UTF8.GetBytes(SessionData)));
            }
            set
            {
                // extract key + serial + deviceid
                if (!string.IsNullOrEmpty(value))
                {
                    var parts = value.Split('|');
                    base.SecretData = value;
                    Serial = parts.Length > 1 ? Encoding.UTF8.GetString(StringToByteArray(parts[1])) : null;
                    DeviceId = parts.Length > 2 ? Encoding.UTF8.GetString(StringToByteArray(parts[2])) : null;
                    SteamData = parts.Length > 3 ? Encoding.UTF8.GetString(StringToByteArray(parts[3])) : string.Empty;

                    if (!string.IsNullOrEmpty(SteamData) && SteamData[0] != '{')
                    {
                        // convert old recovation code into SteamData json
                        SteamData = "{\"revocation_code\":\"" + SteamData + "\"}";
                    }
                    var session = parts.Length > 4 ? Encoding.UTF8.GetString(StringToByteArray(parts[4])) : null;

                    //if (Logger != null)
                    //{
                    //    Logger.Debug("Set Steam data: {0}, Session:{1}", (SteamData ?? string.Empty).Replace("\n", " ").Replace("\r", ""), (SessionData ?? string.Empty).Replace("\n", " ").Replace("\r", ""));
                    //}

                    if (!string.IsNullOrEmpty(session))
                    {
                        SessionData = session;
                    }
                }
                else
                {
                    SecretKey = null;
                    Serial = null;
                    DeviceId = null;
                    SteamData = null;
                    SessionData = null;
                }
            }
        }

        /// <summary>
        /// Get (or create) the current Steam client for this Authenticator
        /// </summary>
        /// <returns>current or new SteamClient</returns>
        public SteamClient GetClient()
        {
            lock (this)
            {
                if (Client == null)
                {
                    Client = new SteamClient(this, SessionData);
                }

                return Client;
            }
        }

        /// <summary>
        /// Perform a request to the Steam WebAPI service
        /// </summary>
        /// <param name="url">API url</param>
        /// <param name="method">GET or POST</param>
        /// <param name="data">Name-data pairs</param>
        /// <param name="cookies">current cookie container</param>
        /// <returns>response body</returns>
        private string Request(string url, string method, NameValueCollection data = null, CookieContainer cookies = null, NameValueCollection headers = null, int timeout = 0)
        {
            // create form-encoded data for query or body
            var query = data == null ? string.Empty : string.Join("&", Array.ConvertAll(data.AllKeys, key => string.Format("{0}={1}", HttpUtility.UrlEncode(key), HttpUtility.UrlEncode(data[key]))));
            if (string.Compare(method, "GET", true) == 0)
            {
                url += (url.IndexOf("?") == -1 ? "?" : "&") + query;
            }

            // call the server
            var request = WebRequest.CreateHttp(url);
            request.Method = method;
            request.Accept = "text/javascript, text/html, application/xml, text/xml, */*";
            request.ServicePoint.Expect100Continue = false;
            request.UserAgent = "Mozilla/5.0 (Linux; U; Android 4.1.1; en-us; Google Nexus 4 - 4.1.1 - API 16 - 768x1280 Build/JRO03S) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30";
            request.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip;
            request.Referer = COMMUNITY_BASE; // + "/mobilelogin?oauth_client_id=DE45CD61&oauth_scope=read_profile%20write_profile%20read_client%20write_client";
            if (headers != null)
            {
                request.Headers.Add(headers);
            }
            if (cookies != null)
            {
                request.CookieContainer = cookies;
            }
            if (timeout != 0)
            {
                request.Timeout = timeout;
            }

            if (string.Compare(method, "POST", true) == 0)
            {
                request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                request.ContentLength = query.Length;

                var requestStream = new StreamWriter(request.GetRequestStream());
                requestStream.Write(query);
                requestStream.Close();
            }

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    LogRequest(method, url, cookies, data, response.StatusCode.ToString() + " " + response.StatusDescription);

                    // OK?
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new InvalidRequestException(string.Format("{0}: {1}", (int)response.StatusCode, response.StatusDescription));
                    }

                    // load the response
                    using (var responseStream = new StreamReader(response.GetResponseStream()))
                    {
                        var responseData = responseStream.ReadToEnd();
                        LogRequest(method, url, cookies, data, responseData);
                        return responseData;
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(method, url, cookies, data, ex);

                if (ex is WebException webException && webException.Response != null && ((HttpWebResponse)webException.Response).StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedRequestException(ex);
                }

                throw new InvalidRequestException(ex.Message, ex);
            }
        }

        /// <summary>
        /// Enroll the authenticator with the server
        /// </summary>
        public bool Enroll(EnrollState state)
        {
            // clear error
            state.Error = null;

            try
            {
                var data = new NameValueCollection();
                var cookies = state.Cookies = state.Cookies ?? new CookieContainer();
                string response;

                if (string.IsNullOrEmpty(state.OAuthToken))
                {
                    // get session
                    if (cookies.Count == 0)
                    {
                        cookies.Add(new Uri(COMMUNITY_BASE + "/"), new Cookie("mobileClientVersion", "3067969+%282.1.3%29"));
                        cookies.Add(new Uri(COMMUNITY_BASE + "/"), new Cookie("mobileClient", "android"));
                        cookies.Add(new Uri(COMMUNITY_BASE + "/"), new Cookie("steamid", ""));
                        cookies.Add(new Uri(COMMUNITY_BASE + "/"), new Cookie("steamLogin", ""));
                        cookies.Add(new Uri(COMMUNITY_BASE + "/"), new Cookie("Steam_Language", "english"));
                        cookies.Add(new Uri(COMMUNITY_BASE + "/"), new Cookie("dob", ""));

                        var headers = new NameValueCollection
                        {
                            { "X-Requested-With", "com.valvesoftware.android.steam.community" }
                        };

                        response = Request("https://steamcommunity.com/mobilelogin?oauth_client_id=DE45CD61&oauth_scope=read_profile%20write_profile%20read_client%20write_client", "GET", null, cookies, headers);
                    }

                    // Steam strips any non-ascii chars from username and password
                    state.Username = Regex.Replace(state.Username, @"[^\u0000-\u007F]", string.Empty);
                    state.Password = Regex.Replace(state.Password, @"[^\u0000-\u007F]", string.Empty);

                    // get the user's RSA key
                    data.Add("username", state.Username);
                    response = Request(COMMUNITY_BASE + "/mobilelogin/getrsakey", "POST", data, cookies);
                    var rsaresponse = JObject.Parse(response);
                    if (!rsaresponse.SelectToken("success").Value<bool>())
                    {
                        throw new InvalidEnrollResponseException("Cannot get steam information for user: " + state.Username);
                    }

                    // encrypt password with RSA key
                    var random = new RNGCryptoServiceProvider();
                    byte[] encryptedPassword;
                    using (var rsa = new RSACryptoServiceProvider())
                    {
                        var passwordBytes = Encoding.ASCII.GetBytes(state.Password);
                        var p = rsa.ExportParameters(false);
                        p.Exponent = StringToByteArray(rsaresponse.SelectToken("publickey_exp").Value<string>());
                        p.Modulus = StringToByteArray(rsaresponse.SelectToken("publickey_mod").Value<string>());
                        rsa.ImportParameters(p);
                        encryptedPassword = rsa.Encrypt(passwordBytes, false);
                    }

                    // login request
                    data = new NameValueCollection
                    {
                        { "password", Convert.ToBase64String(encryptedPassword) },
                        { "username", state.Username },
                        { "twofactorcode", "" },
                        { "emailauth", state.EmailAuthText ?? string.Empty },
                        { "loginfriendlyname", "#login_emailauth_friendlyname_mobile" },
                        { "captchagid", state.CaptchaId ?? "-1" },
                        { "captcha_text", state.CaptchaText ?? "enter above characters" },
                        { "emailsteamid", state.EmailAuthText != null ? state.SteamId ?? string.Empty : string.Empty },
                        { "rsatimestamp", rsaresponse.SelectToken("timestamp").Value<string>() },
                        { "remember_login", "false" },
                        { "oauth_client_id", "DE45CD61" },
                        { "oauth_scope", "read_profile write_profile read_client write_client" },
                        { "donotache", new DateTime().ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds.ToString() }
                    };
                    response = Request(COMMUNITY_BASE + "/mobilelogin/dologin/", "POST", data, cookies);
                    var loginresponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);

                    if (loginresponse.ContainsKey("emailsteamid"))
                    {
                        state.SteamId = loginresponse["emailsteamid"] as string;
                    }

                    // require captcha
                    if (loginresponse.ContainsKey("captcha_needed") && (bool)loginresponse["captcha_needed"])
                    {
                        state.RequiresCaptcha = true;
                        state.CaptchaId = (string)loginresponse["captcha_gid"];
                        state.CaptchaUrl = COMMUNITY_BASE + "/public/captcha.php?gid=" + state.CaptchaId;
                    }
                    else
                    {
                        state.RequiresCaptcha = false;
                        state.CaptchaId = null;
                        state.CaptchaUrl = null;
                        state.CaptchaText = null;
                    }

                    // require email auth
                    if (loginresponse.ContainsKey("emailauth_needed") && (bool)loginresponse["emailauth_needed"])
                    {
                        if (loginresponse.ContainsKey("emaildomain"))
                        {
                            var emaildomain = (string)loginresponse["emaildomain"];
                            if (!string.IsNullOrEmpty(emaildomain))
                            {
                                state.EmailDomain = emaildomain;
                            }
                        }
                        state.RequiresEmailAuth = true;
                    }
                    else
                    {
                        state.EmailDomain = null;
                        state.RequiresEmailAuth = false;
                    }

                    // require email auth
                    state.Requires2FA = loginresponse.ContainsKey("requires_twofactor") && (bool)loginresponse["requires_twofactor"];

                    // if we didn't login, return the result
                    if (!loginresponse.ContainsKey("login_complete") || !(bool)loginresponse["login_complete"] || !loginresponse.ContainsKey("oauth"))
                    {
                        if (!loginresponse.ContainsKey("oauth"))
                        {
                            state.Error = "Invalid response from Steam (No OAuth token)";
                        }
                        if (loginresponse.ContainsKey("message"))
                        {
                            state.Error = (string)loginresponse["message"];
                        }
                        return false;
                    }

                    // get the OAuth token - is stringified json
                    var oauth = (string)loginresponse["oauth"];
                    var oauthjson = JObject.Parse(oauth);
                    state.OAuthToken = oauthjson.SelectToken("oauth_token").Value<string>();
                    if (oauthjson.SelectToken("steamid") != null)
                    {
                        state.SteamId = oauthjson.SelectToken("steamid").Value<string>();
                    }
                }

                // login to webapi
                data.Clear();
                data.Add("access_token", state.OAuthToken);
                response = Request(WEBAPI_BASE + "/ISteamWebUserPresenceOAuth/Logon/v0001", "POST", data);

                var sessionid = cookies.GetCookies(new Uri(COMMUNITY_BASE + "/"))["sessionid"].Value;

                if (!state.RequiresActivation)
                {
                    data.Clear();
                    data.Add("op", "has_phone");
                    data.Add("arg", "null");
                    data.Add("sessionid", sessionid);

                    response = Request(COMMUNITY_BASE + "/steamguard/phoneajax", "POST", data, cookies);
                    var jsonresponse = JObject.Parse(response);
                    var hasPhone = jsonresponse.SelectToken("has_phone").Value<bool>();
                    if (!hasPhone)
                    {
                        state.OAuthToken = null; // force new login
                        state.RequiresLogin = true;
                        state.Cookies = null;
                        state.Error = "Your Steam account must have a SMS-capable phone number attached. Go into Account Details of the Steam client or Steam website and click Add a Phone Number.";
                        return false;
                    }

                    //response = Request(COMMUNITY_BASE + "/steamguard/phone_checksms?bForTwoFactor=1&bRevoke2fOnCancel=", "GET", null, cookies);

                    // add a new authenticator
                    data.Clear();
                    var deviceId = BuildRandomId();
                    data.Add("access_token", state.OAuthToken);
                    data.Add("steamid", state.SteamId);
                    data.Add("authenticator_type", "1");
                    data.Add("device_identifier", deviceId);
                    data.Add("sms_phone_id", "1");
                    response = Request(WEBAPI_BASE + "/ITwoFactorService/AddAuthenticator/v0001", "POST", data);
                    var tfaresponse = JObject.Parse(response);
                    if (response.IndexOf("status") == -1 && tfaresponse.SelectToken("response.status").Value<int>() == 84)
                    {
                        // invalid response
                        state.OAuthToken = null; // force new login
                        state.RequiresLogin = true;
                        state.Cookies = null;
                        state.Error = "Unable to send SMS. Check your phone is registered on your Steam account.";
                        return false;
                    }
                    if (response.IndexOf("shared_secret") == -1)
                    {
                        // invalid response
                        state.OAuthToken = null; // force new login
                        state.RequiresLogin = true;
                        state.Cookies = null;
                        state.Error = "Invalid response from Steam: " + response;
                        return false;
                    }

                    // save data into this authenticator
                    var secret = tfaresponse.SelectToken("response.shared_secret").Value<string>();
                    SecretKey = Convert.FromBase64String(secret);
                    Serial = tfaresponse.SelectToken("response.serial_number").Value<string>();
                    DeviceId = deviceId;
                    state.RevocationCode = tfaresponse.SelectToken("response.revocation_code").Value<string>();

                    // add the steamid into the data
                    var steamdata = JObject.Parse(tfaresponse.SelectToken("response").ToString());
                    if (steamdata.SelectToken("steamid") == null)
                    {
                        steamdata.Add("steamid", state.SteamId);
                    }
                    if (steamdata.SelectToken("steamguard_scheme") == null)
                    {
                        steamdata.Add("steamguard_scheme", "2");
                    }
                    SteamData = steamdata.ToString(Formatting.None);

                    // calculate server drift
                    var servertime = tfaresponse.SelectToken("response.server_time").Value<long>() * 1000;
                    ServerTimeDiff = servertime - CurrentTime;
                    LastServerTime = DateTime.Now.Ticks;

                    state.RequiresActivation = true;

                    return false;
                }

                // finalize adding the authenticator
                data.Clear();
                data.Add("access_token", state.OAuthToken);
                data.Add("steamid", state.SteamId);
                data.Add("activation_code", state.ActivationCode);

                // try and authorize
                var retries = 0;
                while (state.RequiresActivation && retries < ENROLL_ACTIVATE_RETRIES)
                {
                    data.Add("authenticator_code", CalculateCode(false));
                    data.Add("authenticator_time", ServerTime.ToString());
                    response = Request(WEBAPI_BASE + "/ITwoFactorService/FinalizeAddAuthenticator/v0001", "POST", data);
                    var finalizeresponse = JObject.Parse(response);
                    if (response.IndexOf("status") != -1 && finalizeresponse.SelectToken("response.status").Value<int>() == INVALID_ACTIVATION_CODE)
                    {
                        state.Error = "Invalid activation code";
                        return false;
                    }

                    // reset our time
                    if (response.IndexOf("server_time") != -1)
                    {
                        var servertime = finalizeresponse.SelectToken("response.server_time").Value<long>() * 1000;
                        ServerTimeDiff = servertime - CurrentTime;
                        LastServerTime = DateTime.Now.Ticks;
                    }

                    // check success
                    if (finalizeresponse.SelectToken("response.success").Value<bool>())
                    {
                        if (response.IndexOf("want_more") != -1 && finalizeresponse.SelectToken("response.want_more").Value<bool>())
                        {
                            ServerTimeDiff += Period * 1000L;
                            retries++;
                            continue;
                        }
                        state.RequiresActivation = false;
                        break;
                    }

                    ServerTimeDiff += Period * 1000L;
                    retries++;
                }
                if (state.RequiresActivation)
                {
                    state.Error = "There was a problem activating. There might be an issue with the Steam servers. Please try again later.";
                    return false;
                }

                // mark and successful and return key
                state.Success = true;
                state.SecretKey = ByteArrayToString(SecretKey);

                // send confirmation email
                data.Clear();
                data.Add("access_token", state.OAuthToken);
                data.Add("steamid", state.SteamId);
                data.Add("email_type", "2");
                response = Request(WEBAPI_BASE + "/ITwoFactorService/SendEmail/v0001", "POST", data);

                return true;
            }
            catch (UnauthorizedRequestException ex)
            {
                throw new InvalidEnrollResponseException("You are not allowed to add an authenticator. Have you enabled 'community-generated content' in Family View?", ex);
            }
            catch (InvalidRequestException ex)
            {
                throw new InvalidEnrollResponseException("Error enrolling new authenticator", ex);
            }
        }

        /// <summary>
        /// Synchronise this authenticator's time with Steam.
        /// </summary>
        public override void Sync()
        {
            // check if data is protected
            if (SecretKey == null && EncryptedData != null)
            {
                throw new EncryptedSecretDataException();
            }

            // don't retry for 5 minutes
            if (_lastSyncError >= DateTime.Now.AddMinutes(0 - SYNC_ERROR_MINUTES))
            {
                return;
            }

            try
            {
                var response = Request(SYNC_URL, "POST", null, null, null, SYNC_TIMEOUT);
                var json = JObject.Parse(response);

                // get servertime in ms
                var servertime = json.SelectToken("response.server_time").Value<long>() * 1000;

                // get the difference between the server time and our current time
                ServerTimeDiff = servertime - CurrentTime;
                LastServerTime = DateTime.Now.Ticks;

                // clear any sync error
                _lastSyncError = DateTime.MinValue;
            }
            catch (Exception)
            {
                // don't retry for a while after error
                _lastSyncError = DateTime.Now;

                // set to zero to force reset
                //ServerTimeDiff = 0;
            }
        }

        /// <summary>
        /// Calculate the current code for the authenticator.
        /// </summary>
        /// <param name="resyncTime">flag to resync time</param>
        /// <returns>authenticator code</returns>
        protected override string CalculateCode(bool resyncTime = false, long interval = -1)
        {
            // sync time if required
            if (resyncTime || ServerTimeDiff == 0)
            {
                if (interval > 0)
                {
                    ServerTimeDiff = (interval * Period * 1000L) - CurrentTime;
                }
                else
                {
                    Sync();
                }
            }

            var hmac = new HMac(new Sha1Digest());
            hmac.Init(new KeyParameter(SecretKey));

            var codeIntervalArray = BitConverter.GetBytes(CodeInterval);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(codeIntervalArray);
            }
            hmac.BlockUpdate(codeIntervalArray, 0, codeIntervalArray.Length);

            var mac = new byte[hmac.GetMacSize()];
            hmac.DoFinal(mac, 0);

            // the last 4 bits of the mac say where the code starts (e.g. if last 4 bit are 1100, we start at byte 12)
            var start = mac[19] & 0x0f;

            // extract those 4 bytes
            var bytes = new byte[4];
            Array.Copy(mac, start, bytes, 0, 4);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            var fullcode = BitConverter.ToUInt32(bytes, 0) & 0x7fffffff;

            // build the alphanumeric code
            var code = new StringBuilder();
            for (var i = 0; i < CODE_DIGITS; i++)
            {
                code.Append(STEAMCHARS[fullcode % STEAMCHARS.Length]);
                fullcode /= (uint)STEAMCHARS.Length;
            }

            return code.ToString();
        }

        /// <summary>
        /// Create a random Device ID string for Enrolling
        /// </summary>
        /// <returns>Random string</returns>
        private static string BuildRandomId() => "android:" + Guid.NewGuid().ToString();

        /// <summary>
        /// Log an exception from a Request
        /// </summary>
        /// <param name="method">Get or POST</param>
        /// <param name="url">Request URL</param>
        /// <param name="cookies">cookie container</param>
        /// <param name="request">Request data</param>
        /// <param name="ex">Thrown exception</param>
        private static void LogException(string method, string url, CookieContainer cookies, NameValueCollection request, Exception ex)
        {
            var data = new StringBuilder();
            if (cookies != null)
            {
                foreach (Cookie cookie in cookies.GetCookies(new Uri(url)))
                {
                    if (data.Length == 0)
                    {
                        data.Append("Cookies:");
                    }
                    else
                    {
                        data.Append("&");
                    }
                    data.Append(cookie.Name + "=" + cookie.Value);
                }
                data.Append(" ");
            }

            if (request != null)
            {
                foreach (var key in request.AllKeys)
                {
                    if (data.Length == 0)
                    {
                        data.Append("Req:");
                    }
                    else
                    {
                        data.Append("&");
                    }
                    data.Append(key + "=" + request[key]);
                }
                data.Append(" ");
            }

            Logger.Error(ex, "{0}\t{1}\t{2}", method, url, data.ToString());
        }

        /// <summary>
        /// Log a normal response
        /// </summary>
        /// <param name="method">Get or POST</param>
        /// <param name="url">Request URL</param>
        /// <param name="cookies">cookie container</param>
        /// <param name="request">Request data</param>
        /// <param name="response">response body</param>
        private static void LogRequest(string method, string url, CookieContainer cookies, NameValueCollection request, string response)
        {
            var data = new StringBuilder();
            if (cookies != null)
            {
                foreach (Cookie cookie in cookies.GetCookies(new Uri(url)))
                {
                    if (data.Length == 0)
                    {
                        data.Append("Cookies:");
                    }
                    else
                    {
                        data.Append("&");
                    }
                    data.Append(cookie.Name + "=" + cookie.Value);
                }
                data.Append(" ");
            }

            if (request != null)
            {
                foreach (var key in request.AllKeys)
                {
                    if (data.Length == 0)
                    {
                        data.Append("Req:");
                    }
                    else
                    {
                        data.Append("&");
                    }
                    data.Append(key + "=" + request[key]);
                }
                data.Append(" ");
            }

            Logger.Info("{0}\t{1}\t{2}\t{3}", method, url, data.ToString(), response != null ? response.Replace("\n", "\\n").Replace("\r", "") : string.Empty);
        }
    }

    /// <summary>
    /// Our custom exception for the internal Http Request
    /// </summary>
    public class InvalidRequestException : ApplicationException
    {
        public InvalidRequestException(string msg = null, Exception ex = null) : base(msg, ex) { }
    }

    /// <summary>
    /// 403 forbidden responses
    /// </summary>
    public class UnauthorizedRequestException : InvalidRequestException
    {
        public UnauthorizedRequestException(Exception ex = null) : base("Unauthorized", ex) { }
    }
}
