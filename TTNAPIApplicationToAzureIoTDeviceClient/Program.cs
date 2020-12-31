﻿//---------------------------------------------------------------------------------
// Copyright (c) December 2020, devMobile Software
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
//#define DIAGNOSTICS
//#define DIAGNOSTICS_AZURE_IOT_HUB
//#define DIAGNOSTICS_TTN_MQTT
//#define DEVICE_FIELDS_MINIMUM
//#define DEVICE_ATTRIBUTES_DISPLAY
//#define DOWNLINK_MESSAGE_PROPERTIES_DISPLAY
//---------------------------------------------------------------------------------
namespace devMobile.TheThingsNetwork.TTNAPIApplicationToAzureIoTDeviceClient
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Net.Http;
	using System.Runtime.Caching;
	using System.Text;
	using System.Threading.Tasks;

	using Microsoft.Azure.Devices.Client;

	using CommandLine;
	using Newtonsoft.Json;
	using Newtonsoft.Json.Linq;

	using MQTTnet;
	using MQTTnet.Client;
	using MQTTnet.Client.Disconnecting;
	using MQTTnet.Client.Options;
	using MQTTnet.Client.Publishing;
	using MQTTnet.Client.Receiving;

	using devMobile.TheThingsNetwork.API;
	using devMobile.TheThingsNetwork.Models;

	public static class Program
	{
		private static IMqttClient mqttClient = null;
		private static IMqttClientOptions mqttOptions = null;
		private static readonly ObjectCache DeviceClients = MemoryCache.Default;

#if DEVICE_FIELDS_MINIMUM
		private static readonly string[] DevicefieldMaskPaths = { "attributes" };
#else
		private static readonly string[] DevicefieldMaskPaths = { "name", "description", "attributes" };
#endif

		public static async Task Main(string[] args)
		{
			Console.WriteLine("TheThingsNetwork.TTNAPIApplicationToAzureIoTDeviceClient starting");
			Console.WriteLine();

			await Parser.Default.ParseArguments<CommandLineOptions>(args)
				.WithNotParsed(HandleParseError)
				.WithParsedAsync(ApplicationCore);
		}

		private static void HandleParseError(IEnumerable<Error> errors)
		{
			if (errors.IsVersion())
			{
				Console.WriteLine("Version Request");
				return;
			}

			if (errors.IsHelp())
			{
				Console.WriteLine("Help Request");
				return;
			}
			Console.WriteLine("Parser Fail");
		}

		private static async Task ApplicationCore(CommandLineOptions options)
		{
			CacheItemPolicy cacheItemPolicy = new CacheItemPolicy();
			MqttFactory factory = new MqttFactory();
			mqttClient = factory.CreateMqttClient();

#if DIAGNOSTICS
			Console.WriteLine($"baseURL: {options.ApiBaseUrl}");
			Console.WriteLine($"APIKey: {options.ApiKey}");
			Console.WriteLine($"ApplicationID: {options.ApiApplicationID}");
			Console.WriteLine($"AazureIoTHubconnectionString: {options.AzureIoTHubconnectionString}");
			Console.WriteLine();
#endif

			try
			{
				// First configure MQTT, open connection and wire up disconnection handler. 
				mqttOptions = new MqttClientOptionsBuilder()
					.WithTcpServer(options.MqttServerName)
					.WithCredentials(options.MqttApplicationID, options.MqttAccessKey)
					.WithClientId(options.MqttClientID)
					.WithTls()
					.Build();

				mqttClient.UseDisconnectedHandler(new MqttClientDisconnectedHandlerDelegate(e => MqttClientDisconnected(e)));

				await mqttClient.ConnectAsync(mqttOptions);

				// Prepare the HTTP client to be used in the TTN device enumeration
				using (HttpClient httpClient = new HttpClient())
				{
					EndDeviceRegistryClient endDeviceRegistryClient = new EndDeviceRegistryClient(options.ApiBaseUrl, httpClient)
					{
						ApiKey = options.ApiKey
					};

					// Retrieve list of devices page by page
					V3EndDevices endDevices = await endDeviceRegistryClient.ListAsync(
						options.ApiApplicationID, 
						field_mask_paths: DevicefieldMaskPaths, 
						limit: options.DevicePageSize);
					if ((endDevices != null) && (endDevices.End_devices != null)) // If no devices returns null rather than empty list
					{
						foreach (V3EndDevice endDevice in endDevices.End_devices)
						{
							// Display the device info+attributes then connect device to Azure IoT Hub
#if DEVICE_FIELDS_MINIMUM
							Console.WriteLine($"EndDevice ID: {endDevice.Ids.Device_id}");
#else
							Console.WriteLine($"Device ID: {endDevice.Ids.Device_id} Name: {endDevice.Name} Description: {endDevice.Description}");
							Console.WriteLine($"  CreatedAt: {endDevice.Created_at:dd-MM-yy HH:mm:ss} UpdatedAt: {endDevice.Updated_at:dd-MM-yy HH:mm:ss}");
#endif

#if DEVICE_ATTRIBUTES_DISPLAY
							if (endDevice.Attributes != null)
							{
								Console.WriteLine("  EndDevice attributes");

								foreach (KeyValuePair<string, string> attribute in endDevice.Attributes)
								{
									Console.WriteLine($"    Key: {attribute.Key} Value: {attribute.Value}");
								}
							}
#endif
							try
							{
								DeviceClient deviceClient = DeviceClient.CreateFromConnectionString(
									options.AzureIoTHubconnectionString,
									endDevice.Ids.Device_id,
									TransportType.Amqp_Tcp_Only);

								await deviceClient.OpenAsync();

								await deviceClient.SetReceiveMessageHandlerAsync(
									AzureIoTHubClientReceiveMessageHandler,
									new AzureIoTHubReceiveMessageHandlerContext()
									{
										DeviceId = endDevice.Ids.Device_id,
										ApplicationId = endDevice.Ids.Application_ids.Application_id,
									});

								DeviceClients.Add(endDevice.Ids.Device_id, deviceClient, cacheItemPolicy);
							}
							catch( Exception ex)
                     {
								Console.WriteLine($"Azure IoT Hub OpenAsync failed {ex.Message}");
							}
						}
					}
				}

				// At this point all the AzureIoT Hub deviceClients setup and ready to go so can enable MQTT receive
				mqttClient.UseApplicationMessageReceivedHandler(new MqttApplicationMessageReceivedHandlerDelegate(e => MqttClientApplicationMessageReceived(e)));

				// This may shift to individual device subscriptions
				string uplinkTopic = $"v3/{options.MqttApplicationID}/devices/+/up";
				await mqttClient.SubscribeAsync(uplinkTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

				//string queuedTopic = $"v3/{options.MqttApplicationID}/devices/+/queued";
				//await mqttClient.SubscribeAsync(queuedTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

				//string sentTopic = $"v3/{options.MqttApplicationID}/devices/+/sent";
				//await mqttClient.SubscribeAsync(sentTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

				//string ackTopic = $"v3/{options.MqttApplicationID}/devices/+/ack";
				//await mqttClient.SubscribeAsync(ackTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

				//string nackTopic = $"v3/{options.MqttApplicationID}/devices/+/nack";
				//await mqttClient.SubscribeAsync(nackTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

				//string failedTopic = $"v3/{options.MqttApplicationID}/devices/+/failed";
				//await mqttClient.SubscribeAsync(failedTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
			}
			catch(Exception ex)
			{
				Console.WriteLine($"Main {ex.Message}");
				Console.WriteLine("Press any key to exit");
				Console.ReadLine();
				return;
			}

			while (!Console.KeyAvailable)
			{
				Console.Write(".");
				await Task.Delay(1000);
			}

			// Consider ways to mop up connections

			Console.WriteLine("Press any key to exit");
			Console.ReadLine();
		}

		private static async void MqttClientApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs e)
		{
			if (e.ApplicationMessage.Topic.EndsWith("/up", StringComparison.InvariantCultureIgnoreCase))
			{
				await UplinkMessageReceived(e);
			}

			/*
			if (e.ApplicationMessage.Topic.EndsWith("/queued", StringComparison.InvariantCultureIgnoreCase))
			{
				await DownlinkMessageQueued(e);
			}

			if (e.ApplicationMessage.Topic.EndsWith("/sent", StringComparison.InvariantCultureIgnoreCase))
			{
				await DownlinkMessageReceived(e);
			}

			if (e.ApplicationMessage.Topic.EndsWith("/ack", StringComparison.InvariantCultureIgnoreCase))
			{
				await DownlinkMessageAck(e);
			}

			if (e.ApplicationMessage.Topic.EndsWith("/nack", StringComparison.InvariantCultureIgnoreCase))
			{
				await DownlinkMessageNack(e);
			}

			if (e.ApplicationMessage.Topic.EndsWith("/failed", StringComparison.InvariantCultureIgnoreCase))
			{
				await DownlinkMessageFailed(e);
			}
			*/
		}

		static async Task UplinkMessageReceived(MqttApplicationMessageReceivedEventArgs e)
      {
			try
			{
				PayloadUplinkV3 payload = JsonConvert.DeserializeObject<PayloadUplinkV3>(e.ApplicationMessage.ConvertPayloadToString());

				string applicationId = payload.EndDeviceIds.ApplicationIds.ApplicationId;
				string deviceId = payload.EndDeviceIds.DeviceId;
				int port = payload.UplinkMessage.Port;

				Console.WriteLine();
				Console.WriteLine();
				Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} TTN Uplink message");
#if DIAGNOSTICS_TTN_MQTT
				Console.WriteLine($" ClientId:{e.ClientId} Topic:{e.ApplicationMessage.Topic}");
				Console.WriteLine($" Cached: {DeviceClients.Contains(deviceId)}");

				if (payload.UplinkMessage.RXMetadata != null)
				{
					foreach (RxMetadata rxMetaData in payload.UplinkMessage.RXMetadata)
					{
						Console.WriteLine($"  GatewayId", rxMetaData.GatewayIds.GatewayId);
						Console.WriteLine($"  ReceivedAtUTC", rxMetaData.ReceivedAtUtc);
						Console.WriteLine($"  RSSI", rxMetaData.Rssi);
						Console.WriteLine($"  Snr", rxMetaData.Snr);
					}
				}
#endif
				Console.WriteLine($" ApplicationID: {applicationId}");
				Console.WriteLine($" DeviceID: {deviceId}");
				Console.WriteLine($" Port: {port}");
				Console.WriteLine($" Payload raw: {payload.UplinkMessage.PayloadRaw}");

				DeviceClient deviceClient = (DeviceClient)DeviceClients.Get(deviceId);
				if (deviceClient == null)
				{
					Console.WriteLine($" UplinkMessageReceived unknown DeviceID: {deviceId}");
					return;
				}

				JObject telemetryEvent = new JObject();

				telemetryEvent.Add("DeviceID", deviceId);
				telemetryEvent.Add("ApplicationID", applicationId);
				telemetryEvent.Add("Port", port);
				telemetryEvent.Add("PayloadRaw", payload.UplinkMessage.PayloadRaw);

				// If the payload has been unpacked in TTN backend add fields to telemetry event payload
				if (payload.UplinkMessage.PayloadDecoded != null)
				{
					EnumerateChildren(telemetryEvent, payload.UplinkMessage.PayloadDecoded);
				}

				// Send the message to Azure IoT Hub/Azure IoT Central
				using (Message ioTHubmessage = new Message(Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(telemetryEvent))))
				{
					// Ensure the displayed time is the acquired time rather than the uploaded time. 
					//ioTHubmessage.Properties.Add("iothub-creation-time-utc", payloadObject.Metadata.ReceivedAtUtc.ToString("s", CultureInfo.InvariantCulture));
					ioTHubmessage.Properties.Add("ApplicationId", applicationId);
					ioTHubmessage.Properties.Add("DeviceId", deviceId);
					ioTHubmessage.Properties.Add("port", port.ToString());

					await deviceClient.SendEventAsync(ioTHubmessage);
				}
			}
			catch( Exception ex)
         {
				Debug.WriteLine("UplinkMessageReceived failed: {0}", ex.Message);
			}
		}

		private static void EnumerateChildren(JObject jobject, JToken token)
		{
			if (token is JProperty property)
			{
				if (token.First is JValue)
				{
					// Temporary dirty hack for Azure IoT Central compatibility
					if (token.Parent is JObject possibleGpsProperty)
					{
						if (possibleGpsProperty.Path.StartsWith("GPS_", StringComparison.OrdinalIgnoreCase))
						{
							if (string.Compare(property.Name, "Latitude", true) == 0)
							{
								jobject.Add("lat", property.Value);
							}
							if (string.Compare(property.Name, "Longitude", true) == 0)
							{
								jobject.Add("lon", property.Value);
							}
							if (string.Compare(property.Name, "Altitude", true) == 0)
							{
								jobject.Add("alt", property.Value);
							}
						}
					}
					jobject.Add(property.Name, property.Value);
				}
				else
				{
					JObject parentObject = new JObject();
					foreach (JToken token2 in token.Children())
					{
						EnumerateChildren(parentObject, token2);
						jobject.Add(property.Name, parentObject);
					}
				}
			}
			else
			{
				foreach (JToken token2 in token.Children())
				{
					EnumerateChildren(jobject, token2);
				}
			}
		}

		private async static Task AzureIoTHubClientReceiveMessageHandler(Message message, object userContext)
      {
			AzureIoTHubReceiveMessageHandlerContext receiveMessageHandlerConext = (AzureIoTHubReceiveMessageHandlerContext)userContext;
			
			DeviceClient deviceClient = (DeviceClient)DeviceClients.Get(receiveMessageHandlerConext.DeviceId);
			if (deviceClient == null)
			{
				Console.WriteLine($" UplinkMessageReceived unknown DeviceID: {receiveMessageHandlerConext.DeviceId}");
				await deviceClient.RejectAsync(message);
				return;
			}

			using (message)
			{
				Console.WriteLine();
				Console.WriteLine();
				Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} Azure IoT Hub downlink message");
				Console.WriteLine($" ApplicationID: {receiveMessageHandlerConext.ApplicationId}");
				Console.WriteLine($" DeviceID: {receiveMessageHandlerConext.DeviceId}");
#if DIAGNOSTICS_AZURE_IOT_HUB
				Console.WriteLine($" Cached: {DeviceClients.Contains(receiveMessageHandlerConext.DeviceId)}");
				Console.WriteLine($" MessageID: {message.MessageId}");
				Console.WriteLine($" DeliveryCount: {message.DeliveryCount}");
				Console.WriteLine($" EnqueuedTimeUtc: {message.EnqueuedTimeUtc}");
				Console.WriteLine($" SequenceNumber: {message.SequenceNumber}");
				Console.WriteLine($" To: {message.To}");
#endif
				string messageBody = Encoding.UTF8.GetString(message.GetBytes());
				Console.WriteLine($" Body: {messageBody}");
#if DOWNLINK_MESSAGE_PROPERTIES_DISPLAY
				foreach (var property in message.Properties)
				{
					Console.WriteLine($"   Key:{property.Key} Value:{property.Value}");
				}
#endif

				await deviceClient.CompleteAsync(message);

				Console.WriteLine();
			}
		}

		private static async void MqttClientDisconnected(MqttClientDisconnectedEventArgs e)
		{
			Debug.WriteLine($"Disconnected: {e.ReasonCode}");
			await Task.Delay(TimeSpan.FromSeconds(5));

			try
			{
				await mqttClient.ConnectAsync(mqttOptions);
			}
			catch (Exception ex)
			{
				Debug.WriteLine("Reconnect failed: {0}", ex.Message);
			}
		}
	}
}