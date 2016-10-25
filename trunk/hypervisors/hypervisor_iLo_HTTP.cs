using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace hypervisors
{
    /// <summary>
    /// This class represents the HTTP connection to the iLo. It provides very basic functionality, such as power control.
    /// </summary>
    public class hypervisor_iLo_HTTP
    {
        private string _ip;
        private string _username;
        private string _password;

        private string _baseURL;

        private string _sessionKey;
        private CookieContainer _cookies = new CookieContainer();

        public int retries = 10;

        public hypervisor_iLo_HTTP(string ip, string username, string password)
        {
            _ip = ip;
            _username = username;
            _password = password;
            _baseURL = string.Format("https://{0}/json", _ip);
        }

        public void powerOff()
        {
            doRequest("host_power", "hold_power_button");            
        }

        private string doRequest(string pageName, string methodName, bool isPost = true)
        {
            int retriesLeft = retries;
            while (true)
            {
                try
                {
                    try
                    {
                        return _doRequest(pageName, methodName, isPost);
                    }
                    catch (iloNoSessionException)
                    {
                        // Okay, log in again.
                        connect();

                        return _doRequest(pageName, methodName, isPost);
                    }
                }
                catch (Exception)
                {
                    if (retriesLeft-- == 0)
                        throw;
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
            }
        }

        private string _doRequest(string pageName, string methodName, bool isPost = true)
        {
            string url = _baseURL + "/" + pageName;
            HttpWebRequest req = WebRequest.CreateHttp(url);
            req.CookieContainer = _cookies;
            // Don't bother validating the SSL cert. :^)
            req.ServerCertificateValidationCallback += (a, b, c, d) => true;
            if (isPost)
            {
                req.Method = "POST";
                string payload = "{\"method\":\"" + methodName + "\",\"session_key\":\"" + _sessionKey + "\"}";
                Byte[] dataBytes = Encoding.ASCII.GetBytes(payload);
                req.ContentLength = dataBytes.Length;
                using (Stream stream = req.GetRequestStream())
                {
                    stream.Write(dataBytes, 0, dataBytes.Length);
                }
            }
            else
            {
                req.Method = "GET";
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (Stream respStream = resp.GetResponseStream())
                    {
                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            string contentString = respStreamReader.ReadToEnd();

                            if (resp.StatusCode == HttpStatusCode.Forbidden)
                            {
                                ilo_resp_error result = JsonConvert.DeserializeObject<ilo_resp_error>(contentString);
                                if (result.message == "JS_ERR_LOST_SESSION")
                                    throw new iloNoSessionException(result.details);
                            }

                            if (resp.StatusCode != HttpStatusCode.OK)
                                throw new iloException("iLo API call failed, status " + resp.StatusCode + ", URL " + url + " HTTP response body " + contentString);

                            return contentString;
                        }
                    }
                }
            }
            catch (WebException e)
            {
                HttpWebResponse resp = ((HttpWebResponse)e.Response);
                using (Stream respStream = e.Response.GetResponseStream())
                {
                    using (StreamReader respStreamReader = new StreamReader(respStream))
                    {
                        string contentString = respStreamReader.ReadToEnd();

                        if (resp.StatusCode == HttpStatusCode.Forbidden)
                        {
                            ilo_resp_error result = JsonConvert.DeserializeObject<ilo_resp_error>(contentString);
                            if (result.message == "JS_ERR_LOST_SESSION")
                                throw new iloNoSessionException(result.details);
                        }

                        throw new iloException("iLo API call failed, status " + ((HttpWebResponse)e.Response).StatusCode + ", URL " + url + " HTTP response body " + contentString);
                    }
                }
            }
            catch (Exception)
            {
                throw new iloException("iLo API call failed, no response");
            }
        }

        public void connect()
        {
            string url = _baseURL + "/login_session";
            HttpWebRequest req = WebRequest.CreateHttp(url);
            req.Method = "POST";
            req.CookieContainer = _cookies;
            string payload = "{\"method\":\"login\",\"user_login\":\"" + _username + "\",\"password\":\"" + _password + "\"}:";
            Byte[] dataBytes = Encoding.ASCII.GetBytes(payload);
            req.ContentLength = dataBytes.Length;
            // Don't bother validating the SSL cert. :^)
            req.ServerCertificateValidationCallback += (a, b, c, d) => true;
            using (Stream stream = req.GetRequestStream())
            {
                stream.Write(dataBytes, 0, dataBytes.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (Stream respStream = resp.GetResponseStream())
                    {
                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            string contentString = respStreamReader.ReadToEnd();

                            if (resp.StatusCode != HttpStatusCode.OK)
                                throw new Exception("iLo API call failed, status " + resp.StatusCode + ", URL " + url + " HTTP response body " + contentString);

                            ilo_resp_login result = JsonConvert.DeserializeObject<ilo_resp_login>(contentString);

                            _sessionKey = result.session_key;
                        }
                    }
                }
            }

            catch (WebException e)
            {
                using (Stream respStream = e.Response.GetResponseStream())
                {
                    using (StreamReader respStreamReader = new StreamReader(respStream))
                    {
                        string contentString = respStreamReader.ReadToEnd();
                        throw new Exception("iLo API call failed, status " + ((HttpWebResponse)e.Response).StatusCode + ", URL " + url + " HTTP response body " + contentString);
                    }
                }
            }
            catch (Exception)
            {
                throw new Exception("iLo API call failed, no response");
            }            
        }

        public void powerOn()
        {
            doRequest("host_power", "press_power_button");
        }

        public bool getPowerStatus()
        {
            while (true)
            {
                ilo_resp_pwrState pwrResp = JsonConvert.DeserializeObject<ilo_resp_pwrState>(doRequest("host_power", null, isPost: false));

                switch (pwrResp.hostpwr_state.ToUpper())
                {
                    case "ON":
                        return true;
                    case "OFF":
                        return false;
                    default:
                        throw new Exception("Unrecognised power state '" + pwrResp.hostpwr_state + "'");
                }
            }            
        }
    }

    public class ilo_resp_error
    {
        public string message;
        public string details;
    }

    public class ilo_resp_login
    {
        public string session_key;
        public string user_name;
        public string user_account;
    }

    public class ilo_resp_pwrState
    {
        public string hostpwr_state;
    }

    public class iloException : Exception
    {
        public iloException(string msg) : base(msg) { }
    }

    public class iloNoSessionException : iloException
    {
        public iloNoSessionException(string msg) : base(msg) { }
    }
}