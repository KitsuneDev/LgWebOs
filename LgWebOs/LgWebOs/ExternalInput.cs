using Newtonsoft.Json;

namespace LgWebOs
{
    public class ExternalInput
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("icon")]
        public string Icon { get; set; }

        public override string ToString()
        {
            return Id + ":" + Label + ":" + Icon;
        }
    }
}