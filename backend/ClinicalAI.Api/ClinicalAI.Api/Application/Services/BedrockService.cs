using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ClinicalAI.Api.Application.Services
{
    public interface IBedrockService
    {
        Task<string> InvokeModelAsync(string userPrompt);
    }

    public class BedrockService : IBedrockService
    {
        private readonly IAmazonBedrockRuntime _client;
        private const string ModelId = "amazon.nova-pro-v1:0";

        public BedrockService(IAmazonBedrockRuntime client)
        {
            _client = client;
        }

        public async Task<string> InvokeModelAsync(string userPrompt)
        {
            var request = new ConverseRequest
            {
                ModelId = ModelId,
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
