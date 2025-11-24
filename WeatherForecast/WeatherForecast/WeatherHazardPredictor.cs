using Azure.AI.OpenAI.Chat; // <-- Ensure this is included for Uri
using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.Text;
using System.Threading.Tasks; // <-- Ensure this is included for Task

public class WeatherHazardPredictor
{
    // Use 'async Task' here to allow the use of 'await'
    public async Task<ChatMessageContent> Predictor()
    {
        const string endpoint = "https://aifoundry909.services.ai.azure.com/openai/v1/";
        var deploymentName = "Llama-3.3-70B-Instruct";
        const string apiKey = "";

        var relativePath = "weather_log.json";
        var baseDir = Environment.CurrentDirectory;
        var path = Path.Combine(baseDir, relativePath);
        string fileContent = File.ReadAllText(path, Encoding.UTF8);
        ChatClient client = new(
            credential: new ApiKeyCredential(apiKey),
            model: deploymentName,
            options: new OpenAIClientOptions()
            {
                Endpoint = new($"{endpoint}"),
            });

        var textdata = @"You are an expert meteorological analyst. 
You will be provided a JSON lines dataset containing historical short-term hazards forecasts and detected events for one or multiple cities.
Task:
1) Compare recent records in the provided data and detect trends (increasing/decreasing frequency of each hazard type).
2) For the next 7 calendar days starting from the last date in the dataset, predict hazards (zero or more per date) for each city present.
3) For each predicted hazard produce a probability (0-100) that the hazard will occur on that date.
4) Provide a brief reasoning summary for each city (one- or two-sentence), but the final response must be valid JSON only — nothing else.

Output JSON schema (required):
{
  ""predictions"": [
    {
      ""city"": ""<city name or null if missing>"",
      ""prediction_start_date"": ""YYYY-MM-DD"",
      ""predicted_days"": [
        {
          ""date"": ""YYYY-MM-DD"",
          ""predicted_hazards"": [
            { ""type"": ""heat_wave"", ""probability_percent"": 65 },
            { ""type"": ""heavy_precipitation"", ""probability_percent"": 15 }
          ]
        },
        ...
      ],
      ""trend_summary"": ""Concise trend summary for the city""
    }
  ]
}

Important:
- Return **only** JSON matching the schema above.
- Percentages must be integers (0-100).
- If the input file lists multiple repeated entries for the same city, merge them when analyzing trends.
- If `city` field is missing in input records, use null for that prediction entry but still attempt trend detection from available data.
- Keep predicted_days length = 7 (7 calendar days).";
        // Call CreateChatCompletionAsync on the ChatClient
        ChatCompletion completion = client.CompleteChat(
      [
          new SystemChatMessage(textdata),
            new UserChatMessage($"Here is the dataset (JSON lines) to analyze:\n\n{fileContent}")
     ]);

        return completion.Content;
    }
}