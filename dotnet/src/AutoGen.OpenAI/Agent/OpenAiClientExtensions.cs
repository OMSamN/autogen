// Copyright (c) Microsoft Corporation. All rights reserved.
// OpenAIChatAgent.cs

using System;
using System.ClientModel.Primitives;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Core.Pipeline;

namespace AutoGen.OpenAI;

internal static class OpenAiClientExtensions
{
    public static ValueTask<Response<ChatCompletions>> GetChatCompletionsRelaxedSerialisationAsync(
        this OpenAIClient client,
        ChatCompletionsOptions chatCompletionsOptions,
        CancellationToken cancellationToken
    ) =>
        InvokeApiWithRelaxedSerialisationAsync<ChatCompletions>(
            client,
            "chat/completions",
            chatCompletionsOptions,
            cancellationToken
        );

    private static async ValueTask<Response<T>> InvokeApiWithRelaxedSerialisationAsync<T>(
        OpenAIClient client,
        string operationPath,
        object request,
        CancellationToken ct
    )
        where T : class
    {
        using RequestContent content = ToRequestContent(request);
        RequestContext context = FromCancellationToken(ct);
        context.AddPolicy(new RateLimitExceededDetection(), HttpPipelinePosition.PerRetry);

        //using HttpMessage message = InvokeMethod<HttpMessage>(
        //    GetMethodInfo(
        //        typeof(OpenAIClient),
        //        "CreatePostRequestMessage",
        //        BindingFlags.NonPublic | BindingFlags.Instance
        //    ),
        //    client,
        //    request,
        //    content,
        //    context
        //);
        using HttpMessage message = CreatePostRequestMessage(
            client.Pipeline,
            operationPath,
            content,
            context
        );

        var response = await ProcessMessageAsync(client.Pipeline, message, context, ct)
            .ConfigureAwait(false);

        return FromResponse<T>(response);
    }

    private static RequestContent ToRequestContent(object obj)
    {
        var content = new RelaxedUtf8JsonRequestContent();
        ((IJsonModel<ChatCompletionsOptions>)obj).Write(
            content.JsonWriter,
            new ModelReaderWriterOptions("W")
        );
        return content;
    }

    private static HttpMessage CreatePostRequestMessage(
        HttpPipeline pipeline,
        string operationPath,
        RequestContent content,
        RequestContext context
    )
    {
        HttpMessage message = pipeline.CreateMessage(context, _responseClassifier200);
        Request request = message.Request;
        request.Method = RequestMethod.Post;
        //NOTE: Supports OpenAI only, not Azure
        var uri = new RequestUriBuilder();
        uri.Reset(new Uri("https://api.openai.com/v1"));
        uri.AppendPath($"/{operationPath}", false);
        request.Uri = uri;
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Content-Type", "application/json");
        request.Content = content;
        return message;
    }

    private static ValueTask<Response> ProcessMessageAsync(
        HttpPipeline pipeline,
        HttpMessage message,
        RequestContext? requestContext,
        CancellationToken cancellationToken
    )
    {
        Type httpPipelineExtensionsType =
            typeof(OpenAIClient).Assembly.GetType("Azure.Core.HttpPipelineExtensions")
            ?? throw new InvalidOperationException("HttpPipelineExtensions type not found.");
        var processMessageAsyncMethodInfo = GetMethodInfo(
            httpPipelineExtensionsType,
            nameof(ProcessMessageAsync),
            BindingFlags.Public | BindingFlags.Static
        );
        return InvokeMethod<ValueTask<Response>>(
            processMessageAsyncMethodInfo,
            null,
            pipeline,
            message,
            requestContext,
            cancellationToken
        );
    }

    private static Response<T> FromResponse<T>(Response response)
        where T : class
    {
        var fromResponseMethodInfo = GetMethodInfo(
            typeof(T),
            nameof(FromResponse),
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var tInstance = InvokeMethod<T>(fromResponseMethodInfo, null, response);
        return Response.FromValue(tInstance, response);
    }

    private static MethodInfo GetMethodInfo(
        Type type,
        string methodName,
        BindingFlags bindingFlags
    ) =>
        type.GetMethod(methodName, bindingFlags)
        ?? throw new InvalidOperationException($"Could not find the internal {methodName} method.");

    private static T InvokeMethod<T>(
        MethodInfo methodInfo,
        object? obj,
        params object?[]? parameters
    ) =>
        (T)methodInfo.Invoke(obj, parameters)
        ?? throw new InvalidOperationException($"{methodInfo.Name} returned invalid data.");

    private static RequestContext DefaultRequestContext = new RequestContext();

    private static RequestContext FromCancellationToken(
        CancellationToken cancellationToken = default
    )
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return DefaultRequestContext;
        }

        return new RequestContext() { CancellationToken = cancellationToken };
    }

    private static readonly ResponseClassifier _responseClassifier200 = new StatusCodeClassifier(
        stackalloc ushort[] { 200 }
    );

    private class RelaxedUtf8JsonRequestContent : RequestContent
    {
        private readonly MemoryStream _stream;
        private readonly RequestContent _content;

        public RelaxedUtf8JsonRequestContent()
        {
            _stream = new MemoryStream();
            _content = Create(_stream);
            JsonWriter = new Utf8JsonWriter(
                _stream,
                new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
            //This is encoding, I want to avoid encoding:
            //new JsonWriterOptions
            //{
            //    Encoder = JavaScriptEncoder.Create(
            //        System.Text.Unicode.UnicodeRange.Create('`', '`')
            //    )
            //}
            );

            //No combination of Allow, Forbid, or nothing works with this
            //var tes = new TextEncoderSettings();
            //tes.ForbidCharacter('`');
            //tes.AllowCharacter('`');
            //new JsonWriterOptions { Encoder = JavaScriptEncoder.Create(tes) }

            //Internal extension method:
            //jsonWriter.WriteObjectValue<ChatCompletionsOptions>(
            //    settings,
            //    new ModelReaderWriterOptions("W")
            //);
            //Order or precedence:
            /*
             *      case IJsonModel<T> jsonModel:
                        jsonModel.Write(writer, options ?? WireOptions);
                        break;
                    case IUtf8JsonSerializable serializable:
                        serializable.Write(writer);
                        break;
    */
            //Highest precedence, which is causing issues:
            //((IJsonModel<ChatCompletionsOptions>)settings).Write(
            //    jsonWriter,
            //    new ModelReaderWriterOptions("J")
            //);

            //Second precendence, but the implementation of this actually casts it to IJsonModel<T> and calls Write... so no different to the earlier code, although the earlier one does allow overriding the modelreaderoptions
            //((IUtf8JsonSerializable)settings).Write(jsonWriter);
        }

        public Utf8JsonWriter JsonWriter { get; }

        public override async Task WriteToAsync(
            Stream stream,
            CancellationToken cancellationToken = default
        )
        {
            await JsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _content.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        public override void WriteTo(Stream stream, CancellationToken cancellationToken = default)
        {
            JsonWriter.Flush();
            _content.WriteTo(stream, cancellationToken);
        }

        public override bool TryComputeLength(out long length)
        {
            length = JsonWriter.BytesCommitted + JsonWriter.BytesPending;
            return true;
        }

        public override void Dispose()
        {
            JsonWriter.Dispose();
            _content.Dispose();
            _stream.Dispose();
        }

        public override string ToString()
        {
            JsonWriter.Flush();
            _stream.Position = 0;
            using var sr = new StreamReader(
                _stream,
                new UTF8Encoding(false),
                false,
                1024,
                leaveOpen: true
            );
            return sr.ReadToEnd();
        }
    }

    private class RateLimitExceededDetection : HttpPipelinePolicy
    {
        public override void Process(
            HttpMessage message,
            ReadOnlyMemory<HttpPipelinePolicy> pipeline
        )
        {
            ProcessNext(message, pipeline);
            OnResponseReceived(message.Response.Headers);
        }

        public override async ValueTask ProcessAsync(
            HttpMessage message,
            ReadOnlyMemory<HttpPipelinePolicy> pipeline
        )
        {
            await ProcessNextAsync(message, pipeline);
            OnResponseReceived(message.Response.Headers);
        }

        private void OnResponseReceived(ResponseHeaders headers)
        {
            var rateLimitInfo = new RateLimitInfo(
                headers.TryGetValue("Date", out var dateStr)
                && DateTimeOffset.TryParse(dateStr, out var timeStamp)
                    ? timeStamp
                    : null,
                headers.TryGetValue("x-ratelimit-limit-requests", out var limitRequestsStr)
                && int.TryParse(limitRequestsStr, out var limitRequests)
                    ? limitRequests
                    : null,
                headers.TryGetValue("x-ratelimit-limit-tokens", out var limitTokensStr)
                && int.TryParse(limitTokensStr, out var limitTokens)
                    ? limitTokens
                    : null,
                headers.TryGetValue("x-ratelimit-remaining-requests", out var remainingRequestsStr)
                && int.TryParse(remainingRequestsStr, out var remainingRequests)
                    ? remainingRequests
                    : null,
                headers.TryGetValue("x-ratelimit-remaining-tokens", out var remainingTokensStr)
                && int.TryParse(remainingTokensStr, out var remainingTokens)
                    ? remainingTokens
                    : null,
                headers.TryGetValue("x-ratelimit-reset-requests", out var resetRequestsStr)
                && TimeSpan.TryParse(resetRequestsStr, out var resetRequests)
                    ? resetRequests
                    : null,
                headers.TryGetValue("x-ratelimit-reset-tokens", out var resetTokensStr)
                && TimeSpan.TryParse(resetTokensStr, out var resetTokens)
                    ? resetTokens
                    : null
            );

            if (rateLimitInfo.IsExceeded)
            {
                throw new RateLimitExceededException(rateLimitInfo);
            }
        }
    }
}
