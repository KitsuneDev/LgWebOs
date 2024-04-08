using Newtonsoft.Json;

namespace LgWebOs
{
    public class App
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("icon")]
        public string Icon { get; set; }

        public override string ToString()
        {
            return Id + ":" + Title + ":" + Icon;
        }
    }
}