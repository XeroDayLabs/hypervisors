using System;
using System.Net;

namespace hypervisors
{
    public class nasAccessException : Exception
    {
        public nasAccessException(string e) : base(e) { }

        public nasAccessException() : base() { }

        public static Exception create(HttpWebResponse resp, string url, string contentString)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.Conflict:
                    return new nasConflictException("FreeNAS API call failed with 'conflict' status. URL " + url + " HTTP response body " + contentString);
                case HttpStatusCode.NotFound:
                    return new nasNotFoundException("FreeNAS API call failed with 'not found' status. URL " + url + " HTTP response body " + contentString);
                default:
                    return new nasAccessException("FreeNAS API call failed, status " + resp.StatusCode + ", URL " + url +  " HTTP response body " + contentString);
            }
        }
    }
}