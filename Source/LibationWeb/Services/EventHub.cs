using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace LibationWeb.Services;

/// <summary>JSON the API, the event stream and the tests all agree on.</summary>
public static class Json
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};
}

/// <summary>
/// Fan-out for Server-Sent Events. Publishers hand in a typed payload; every open <c>/api/events</c>
/// connection gets the same pre-rendered frame. A slow client drops its oldest frames rather than
/// holding up a download's progress for everyone else.
/// </summary>
public sealed class EventHub
{
	private readonly ConcurrentDictionary<Guid, Channel<string>> subscribers = new();

	public int SubscriberCount => subscribers.Count;

	public (Guid Id, ChannelReader<string> Reader) Subscribe()
	{
		var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = false,
		});
		var id = Guid.NewGuid();
		subscribers[id] = channel;
		return (id, channel.Reader);
	}

	public void Unsubscribe(Guid id)
	{
		if (subscribers.TryRemove(id, out var channel))
			channel.Writer.TryComplete();
	}

	public void Publish(string eventType, object payload)
	{
		if (subscribers.IsEmpty)
			return;

		var frame = Frame(eventType, payload);
		foreach (var subscriber in subscribers.Values)
			subscriber.Writer.TryWrite(frame);
	}

	public static string Frame(string eventType, object payload)
		=> $"event: {eventType}\ndata: {JsonSerializer.Serialize(payload, Json.Options)}\n\n";
}
