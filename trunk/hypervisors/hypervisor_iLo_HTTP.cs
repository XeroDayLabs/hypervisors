using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace hypervisors
{
    /// <summary>
    /// This class represents the HTTP connection to the iLo. It provides very basic functionality, such as power control.
    /// </summary>
    public class hypervisor_iLo_HTTP : IDisposable
    {
        private string _ip;
        private string _username;
        private string _password;

        private string _baseURL;

        public string _sessionKey;
        private CookieContainer _cookies = new CookieContainer();

        public int retries = 10;

        public bool logoutOnDisposal = true;

        public hypervisor_iLo_HTTP(string ip, string username, string password)
        {
            _ip = ip;
            _username = username;
            _password = password;
            _baseURL = string.Format("https://{0}/json", _ip);
        }

        public void powerOff()
        {
            if (getPowerStatus() == false)
                return;

            doRequest("host_power", "hold_power_button");
        }

        private string doRequest(string pageName, string methodName, bool isPost = true)
        {
            int retriesLeft = retries;
            while (true)
            {
                try
                {
                    if (_cookies.Count == 0)
                        connect();

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
#pragma warning disable 0618  // CertificatePolicy is obselete, but we use it because the alternative doesn't work in Mono.
            ServicePointManager.CertificatePolicy = new NoCheckCertPolicy();
#pragma warning restore 0618
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
                using (HttpWebResponse resp = (HttpWebResponse) req.GetResponse())
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
                HttpWebResponse resp = ((HttpWebResponse) e.Response);
                using (Stream respStream = e.Response.GetResponseStream())
                {
                    using (StreamReader respStreamReader = new StreamReader(respStream))
                    {
                        string contentString = respStreamReader.ReadToEnd();
                        if (resp.StatusCode == HttpStatusCode.Forbidden)
                        {
                            ilo_resp_error result = JsonConvert.DeserializeObject<ilo_resp_error>(contentString);
                            if (result != null && result.message == "JS_ERR_LOST_SESSION")
                                throw new iloNoSessionException(result.details);
                        }

                        throw new iloException("iLo API call failed, status " + ((HttpWebResponse) e.Response).StatusCode + ", URL " + url + " HTTP response body " + contentString);
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
            int retriesLeft = retries;
            while (true)
            {
                try
                {
                    _connect();
                    return;
                }
                catch (iloException)
                {
                    if (retriesLeft-- == 0)
                        throw;

                    Thread.Sleep(TimeSpan.FromSeconds(3));
                }
            }
        }

        public void _connect()
        {
            string url = _baseURL + "/login_session";
            HttpWebRequest req = WebRequest.CreateHttp(url);
            req.Method = "POST";
            req.CookieContainer = _cookies;
            string payload = "{\"method\":\"login\",\"user_login\":\"" + _username + "\",\"password\":\"" + _password + "\"}:";
            Byte[] dataBytes = Encoding.ASCII.GetBytes(payload);
            req.ContentLength = dataBytes.Length;
            // Don't bother validating the SSL cert. :^)
#pragma warning disable 0618  // CertificatePolicy is obselete, but we use it because the alternative doesn't work in Mono.
            ServicePointManager.CertificatePolicy = new NoCheckCertPolicy();
#pragma warning restore 0618
            using (Stream stream = req.GetRequestStream())
            {
                stream.Write(dataBytes, 0, dataBytes.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse) req.GetResponse())
                {
                    using (Stream respStream = resp.GetResponseStream())
                    {
                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            string contentString = respStreamReader.ReadToEnd();

                            if (resp.StatusCode != HttpStatusCode.OK)
                                throw new iloException("iLo API call failed, status " + resp.StatusCode + ", URL " + url + " HTTP response body " + contentString);

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
                        if ((e.Response is HttpWebResponse) &&
                            ((HttpWebResponse) e.Response).StatusCode == HttpStatusCode.Forbidden)
                        {
                            // Either our login details are incorrect, or we have failed login so many times that the iLo has blocked this IP
                            // for a period of time.
                            throw new nonRetryableIloException();
                        }

                        string contentString = respStreamReader.ReadToEnd();
                        throw new iloException("iLo API call failed, status " + ((HttpWebResponse) e.Response).StatusCode + ", URL " + url + " HTTP response body " + contentString);
                    }
                }
            }
            catch (Exception e)
            {
                throw new iloException(e);
            }
        }

        public void logout()
        {
            doRequest("login_session", "logout");
        }

        public void powerOn()
        {
            if (getPowerStatus() == true)
                return;

            doRequest("host_power", "press_power_button");
        }

        public int getCurrentPowerUseW()
        {
            ilo_resp_powerreadings pwrResp = JsonConvert.DeserializeObject<ilo_resp_powerreadings>(doRequest("power_readings", null, isPost: false));

            return pwrResp.present_power_reading;
        }

        public ilo_resp_healthfans getHealthOfFans()
        {
            ilo_resp_healthfans pwrResp = JsonConvert.DeserializeObject<ilo_resp_healthfans>(doRequest("health_fans", null, isPost: false));
            return pwrResp;
        }

        public ilo_resp_healthtemps getHealthOfTemps()
        {
            ilo_resp_healthtemps tmpResp = JsonConvert.DeserializeObject<ilo_resp_healthtemps>(doRequest("health_temperature", null, isPost: false));
            return tmpResp;
        }

        public ilo_resp_healthPSUs getHealthOfPSUs()
        {
            ilo_resp_healthPSUs tmpResp = JsonConvert.DeserializeObject<ilo_resp_healthPSUs>(doRequest("health_power_supply", null, isPost: false));
            return tmpResp;
        }

        public bool getPowerStatus()
        {
            ilo_resp_pwrState pwrResp = JsonConvert.DeserializeObject<ilo_resp_pwrState>(doRequest("host_power", null, isPost: false));

            switch (pwrResp.hostpwr_state.ToUpper())
            {
                case "ON":
                case "RESET":
                    // Machine is coming up 
                    return true;
                case "OFF":
                    return false;
                default:
                    throw new Exception("Unrecognised power state '" + pwrResp.hostpwr_state + "'");
            }
        }

        public void Dispose()
        {
            try
            {
                if (logoutOnDisposal)
                    _doRequest("login_session", "logout");
            }
            catch (Exception)
            {
                // .. oh well ..
            }
        }

        public string makeHPLOLink()
        {
            return String.Format("hplocons://addr={0}&name={1}&sessionkey={2}", _ip, _username, _sessionKey);
        }
    }

    public class nonRetryableIloException : Exception
    {
    }

    public class NoCheckCertPolicy : ICertificatePolicy
    {
        public bool CheckValidationResult(ServicePoint srvPoint, X509Certificate certificate, WebRequest request, int certificateProblem)
        {
            return true;
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

    public class ilo_resp_healthfan
    {
        public string label;
        public string location;
        public string status;
        public string speed;
    }

    public class ilo_resp_healthfans
    {
        public string hostpwr_state;
        public ilo_resp_healthfan[] fans;
    }

    public class ilo_resp_healthtemp
    {
        public string label;
        public int xposition;
        public int yposition;
        public string location;
        public string status;
        public int currentreading;
        public int caution;
        public int critical;
        public string temp_unit;
    }

    public class ilo_resp_healthtemps
    {
        public string hostpwr_state;
        public ilo_resp_healthtemp[] temperature;
    }

    public class ilo_resp_healthPSU
    {
        public string label;
        public string location;
        public string status;
        public string power;
    }

    public class ilo_resp_healthPSUs
    {
        public ilo_resp_healthPSU[] power_supplies;
    }

    public class ilo_resp_powerreadings
    {
        public string hostpwr_state;
        public string fwver;
        public int present_power_reading;
        public int average_power_reading;
        public int maximum_power_reading;
        public int minimum_power_reading;
        public string power_unit;
        public string hem_mode;
        public string enable_spsm;
    }

    public class ilo_resp_powersummary
    {
        public string hostpwr_state;
        public int last_avg_pwr_accum;
        public int last_5min_avg;
        public int last_5min_peak;
        public int _24hr_average;
        public int _24hr_peak;
        public int _24hr_min;
        public int _24hr_max_cap;
        public int _24hr_max_temp;
        public int _20min_average;
        public int _20min_peak;
        public int _20min_min;
        public int _20min_max_cap;
        public int max_measured_wattage;
        public int min_measured_wattage;
        public int volts;
        public int power_cap;
        public string power_cap_mode;
        public string power_regulator_mode;
        public int power_supply_capacity;
        public int power_supply_input_power;
        public int num_valid_history_samples;
        public int num_valid_fast_history_samples;
        public int powerreg;
    }

    public class ilo_resp_pwrState
    {
        public string hostpwr_state;
    }

    public class iloException : Exception
    {
        public iloException(string msg)
            : base(msg)
        {
        }

        public iloException(Exception innerException)
            : base("see innerException", innerException)
        {

        }
    }

    public class iloNoSessionException : iloException
    {
        public iloNoSessionException(string msg)
            : base(msg)
        {
        }
    }
}