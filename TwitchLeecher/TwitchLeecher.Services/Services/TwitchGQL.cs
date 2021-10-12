using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace TwitchLeecher.Services.Services
{
    internal class TwitchGQL
    {
        private const string URL = "https://gql.twitch.tv/gql";

        private const string CLIENT_ID_HEADER = "Client-ID";
        private const string CLIENT_ID = "kimne78kx3ncx6brgo4mv6wki5h1ko";
        private const string CONTENT_TYPE_HEADER = "Content-Type";
        private const string CONTENT_TYPE = "application/json";

        // In the absence of an officially documented value,
        // this what the twitch.tv website uses...
        private const int PAGINATION_PER_PAGE = 30;

        private static WebClient CreateGQLWebClient()
        {
            using (WebClient gql = new WebClient())
            {
                gql.Headers.Add(CLIENT_ID_HEADER, CLIENT_ID);
                gql.Headers.Add(CONTENT_TYPE_HEADER, CONTENT_TYPE);
                gql.Encoding = Encoding.UTF8;

                return gql;
            }
        }

        internal static List<JObject> RunPaginatedPersistedQuery(string operationName, JObject variables, string sha256Hash)
        {
            List<JObject> allData = new List<JObject>();

            bool moreData;
            string cursor = null;

            variables.Remove("limit");
            variables.Add("limit", PAGINATION_PER_PAGE);

            do
            {
                variables.Remove("cursor");
                if (cursor != null) variables.Add("cursor", cursor);

                JObject response = RunPersistedQuery(operationName, variables, sha256Hash);

                JArray thisData = response.SelectToken("$..edges").Value<JArray>();
                moreData = response.SelectToken("$..hasNextPage").Value<bool>();

                foreach (JObject thisDataChild in thisData.Children())
                {
                    cursor = thisDataChild.Value<string>("cursor");
                    allData.Add(thisDataChild.Value<JObject>("node"));
                }
            } while (moreData);

            return allData;
        }

        internal static JObject RunPersistedQuery(string operationName, JObject variables, string sha256Hash)
        {
            using (WebClient gql = CreateGQLWebClient())
            {
                string query = "[{\"operationName\":\"" + operationName + "\"," +
                "\"variables\":" + variables.ToString(Formatting.None) + "," +
                "\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"" + sha256Hash + "\"}}}]";

                string response = gql.UploadString(URL, query);

                return JArray.Parse(response).Value<JObject>(0);
            }
        }

        internal static JObject RunQuery(string query)
        {
            string fullQuery = "{\"query\":\"query { " + query.Replace("\"", "\\\"") + " }\"}";

            using (WebClient gql = CreateGQLWebClient())
            {
                string response = gql.UploadString(URL, fullQuery);

                return JObject.Parse(response).Value<JObject>("data");
            }
        }
    }
}