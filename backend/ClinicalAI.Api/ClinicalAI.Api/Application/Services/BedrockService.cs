using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ClinicalAI.Api.Application.Services
{
    public interface IBedrockService
    {
        Task<string> InvokeModelAsync(string userPrompt);

        /// <summary>Reported alongside generated text so an answer can be traced to
        /// the model that produced it - Part 6 swaps models and the outputs must be
        /// distinguishable.</summary>
        string ModelId { get; }
    }

    public class BedrockService : IBedrockService
    {
        private readonly IAmazonBedrockRuntime _client;
        private const string DefaultModelId = "amazon.nova-pro-v1:0";

        public string ModelId => DefaultModelId;

        public BedrockService(IAmazonBedrockRuntime client)
        {
            _client = client;
        }

        public async Task<string> InvokeModelAsync(string userPrompt)
        {
            var request = new ConverseRequest
            {
                ModelId = DefaultModelId,
                Messages = new List<Message>
            {
                new Message
                {
                    Role = ConversationRole.User,
                    Content = new List<ContentBlock>
                    {
                        new ContentBlock { Text = userPrompt }
                    }
                }
            },
                InferenceConfig = new InferenceConfiguration
                {
                    MaxTokens = 512,
                    Temperature = 0.3f
                },

                // GuardrailConfig = new GuardrailConfiguration
                // {
                //     GuardrailIdentifier = "dnyegc71wdpn",
                //     GuardrailVersion = "1", // use a numbered published version in production
                //     Trace = "enabled"
                // }
            };

            var response = await _client.ConverseAsync(request);

            return response.Output.Message.Content[0].Text;
        }
    }
}
