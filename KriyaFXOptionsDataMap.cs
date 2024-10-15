#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Script.Serialization; // For JavaScriptSerializer
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using System.Net.WebSockets;
using System.Threading;
using System.IO;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
	public class KriyaFXOptionsMap : Indicator
	{
		private HttpClient httpClient;
		private string bearerToken;
		private JavaScriptSerializer jsonSerializer;
		private List<double> strikeLevels = new List<double>();
		private List<double> indexStrikes = new List<double>();
		private double currentPrice;

		// SharpDX Resources
		private SharpDX.DirectWrite.TextFormat textFormat;
		private SharpDX.Direct2D1.Brush textBrush;
		private SharpDX.Direct2D1.Brush strikeLineBrush;

		// Mutex for thread safety
		private object renderLock = new object();

		private double futurePrice;
		private double indexPrice;
		private double ratio;

		private List<double> netAskVolumes = new List<double>();

		private double totalAskVolume;
		private double totalGexVolume;
		private SharpDX.DirectWrite.TextFormat tableTitleFormat;
		private SharpDX.DirectWrite.TextFormat tableContentFormat;
		private SharpDX.Direct2D1.Brush tableBorderBrush;
		private SharpDX.Direct2D1.Brush tableBackgroundBrush;

		private double expectedMove;
		private double expectedMaxPrice;
		private double expectedMinPrice;
		private bool isLoggedIn = false;
		private bool isDataFetched = false;
		private long lastUpdateTimestamp;
		private string underlyingSymbol;
		
		private ClientWebSocket webSocket;
		private CancellationTokenSource cts;
		private string authToken;

		private List<string> messageChunks = new List<string>();

		protected override void OnStateChange()
		{
			try
			{
				if (State == State.SetDefaults)
				{
					Description									= @"Retrieves and plots Options Data from the KriyaFX service";
					Name										= "KriyaFXOptionsMap";
					Calculate									= Calculate.OnBarClose;
					IsOverlay									= true;
					DisplayInDataBox							= true;
					DrawOnPricePanel							= true;
					DrawHorizontalGridLines						= true;
					DrawVerticalGridLines						= true;
					PaintPriceMarkers							= true;
					ScaleJustification							= NinjaTrader.Gui.Chart.ScaleJustification.Right;
					IsSuspendedWhileInactive				= true;
					Username				= string.Empty;
					Password					= string.Empty;
					WebSocketUrl = "ws://localhost:3000";
					Print("KriyaFXOptionsMap: SetDefaults completed");
				}
				else if (State == State.Configure)
				{
					httpClient = new HttpClient();
					jsonSerializer = new JavaScriptSerializer();
					webSocket = new ClientWebSocket();
					cts = new CancellationTokenSource();
					Print("KriyaFXOptionsMap: Configure completed");
				}
				else if (State == State.DataLoaded)
				{
					Print("KriyaFXOptionsMap: DataLoaded - Initiating login");
					// Only initiate login here, not WebSocket connection
					Task.Run(async () =>
					{
						try
						{
							await Login();
						}
						catch (Exception ex)
						{
							Print("Error in Login: " + ex.Message);
						}
					}).Wait();
					Print("KriyaFXOptionsMap: DataLoaded - Login initiated");
				}
				else if (State == State.Historical)
				{
					Print("KriyaFXOptionsMap: Historical state reached");
				}
				else if (State == State.Realtime)
				{
					Print("KriyaFXOptionsMap: Realtime state reached - Initiating WebSocket connection");
					// Initiate WebSocket connection here
					Task.Run(async () =>
					{
						try
						{
							await ConnectWebSocket();
						}
						catch (Exception ex)
						{
							Print("Error in ConnectWebSocket: " + ex.Message);
						}
					}).Wait();
				}
				else if (State == State.Terminated)
				{
					if (httpClient != null)
					{
						httpClient.Dispose();
					}
					DisposeSharpDXResources();
					DisconnectWebSocket();
					Print("KriyaFXOptionsMap: Terminated - Resources disposed");
				}
			}
			catch (Exception ex)
			{
				Print("Error in OnStateChange: " + ex.Message);
				if (ex.InnerException != null)
				{
					Print("Inner Exception: " + ex.InnerException.Message);
				}
			}
		}

		protected override void OnBarUpdate() { }

		private async Task<bool> Login()
		{
			try
			{
				Print("Attempting login...");

				var loginData = new
				{
					username = Username,
					password = Password
				};

				var content = new StringContent(jsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
				var request = new HttpRequestMessage(HttpMethod.Post, "https://kriyafx.de/api/login")
				{
					Content = content
				};

				var response = await httpClient.SendAsync(request);

				if (response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync();
					var tokenObject = jsonSerializer.Deserialize<Dictionary<string, object>>(responseContent);
					bearerToken = tokenObject["token"].ToString();
					Print("Login successful. Bearer token received.");
					return true;
				}
				else
				{
					Print("Login failed. Status code: " + response.StatusCode);
					return false;
				}
			}
			catch (Exception ex)
			{
				Print("Error during login: " + ex.Message);
				return false;
			}
		}

		private async Task InitializeConnectionAsync()
		{
			try
			{
				if (await Login())
				{
					await ConnectWebSocket();
				}
				else
				{
					Print("Failed to login. WebSocket connection not established.");
				}
			}
			catch (Exception ex)
			{
				Print("Error in InitializeConnectionAsync: " + ex.Message);
				if (ex.InnerException != null)
				{
					Print("Inner Exception: " + ex.InnerException.Message);
				}
			}
		}

		private void ProcessOptionsData(string json)
		{
			try
			{
				Print("Processing options data...");
				var data = jsonSerializer.Deserialize<Dictionary<string, object>>(json);
				
				if (!data.ContainsKey("Options"))
				{
					Print("Options data not found in the response.");
					return;
				}

				var options = data["Options"] as Dictionary<string, object>;
				if (options == null || !options.ContainsKey("Data"))
				{
					Print("Options or Data not found in the response.");
					return;
				}

				if (options.ContainsKey("Updated"))
				{
					lastUpdateTimestamp = Convert.ToInt64(options["Updated"]);
				}

				if (options.ContainsKey("Symbol"))
				{
					underlyingSymbol = options["Symbol"].ToString();
				}

				if (options.ContainsKey("ExpectedMove"))
				{
					expectedMove = Convert.ToDouble(options["ExpectedMove"]);
					indexPrice = Convert.ToDouble(data["Price"]);
					expectedMaxPrice = indexPrice + expectedMove;
					expectedMinPrice = indexPrice - expectedMove;
					Print("Expected Move: " + expectedMove);
					Print("Expected Max Price: " + expectedMaxPrice);
					Print("Expected Min Price: " + expectedMinPrice);
				}				

				var dataJson = jsonSerializer.Serialize(options["Data"]);
				var optionsData = jsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataJson);

				Print("Options Data count: " + optionsData.Count);

				strikeLevels.Clear();
				indexStrikes.Clear();
				netAskVolumes.Clear();

				// Calculate future price and ratio
				futurePrice = GetCurrentAsk() - TickSize;
				indexPrice = Convert.ToDouble(data["Price"]);
				ratio = futurePrice / indexPrice;
				double roundedIndexStrike = Math.Floor(indexPrice);

				Print("Future Price: " + futurePrice);
				Print("Index Price: " + indexPrice);
				Print("Index to Futures Ratio: " + ratio);

				foreach (var item in optionsData)
				{
				
					if (item.ContainsKey("strike") && item.ContainsKey("Net_ASK_Volume"))
					{
						double indexStrike = Convert.ToDouble(item["strike"]);
						double futureStrike = indexStrike * ratio;
						double netAskVolume = Convert.ToDouble(item["Net_ASK_Volume"]);

						indexStrikes.Add(indexStrike);
						strikeLevels.Add(futureStrike);
						netAskVolumes.Add(netAskVolume);

						Print("Added index strike: " + indexStrike + ", future strike: " + futureStrike + ", Net Ask Volume: " + netAskVolume);
					}
				}

				if (options.ContainsKey("Total_ASK_Volume"))
				{
					totalAskVolume = Convert.ToDouble(options["Total_ASK_Volume"]);
				}
				if (options.ContainsKey("Total_GEX_Volume"))
				{
					totalGexVolume = Convert.ToDouble(options["Total_GEX_Volume"]);
				}

				// Use Dispatcher to update the UI
				Dispatcher.InvokeAsync(() =>
				{
					// Invalidate the chart to trigger OnRender
					if (ChartControl != null)
					{
						ChartControl.InvalidateVisual();
						Print("Chart invalidated for redraw");
					}
					else
					{
						Print("ChartControl is null");
					}
				});
			}
			catch (Exception ex)
			{
				Print("Error processing options data: " + ex.Message);
			}
		}

		private string FormatTimestamp(long unixTimestamp)
		{
			DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
			return dateTimeOffset.LocalDateTime.ToString("dd/MM/yy, HH:mm:ss");
		}

		public override void OnRenderTargetChanged()
		{
			// Dispose existing resources
			DisposeSharpDXResources();

			if (RenderTarget != null)
			{
				// Initialize SharpDX resources
				textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
				tableTitleFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 14);
				tableContentFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
			
				textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
				strikeLineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.DodgerBlue);
				tableBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
				tableBackgroundBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(0, 0, 0, 192)); // More opaque black (75% opacity)
			}
		}

		private void DisposeSharpDXResources()
		{
			if (textBrush != null) { textBrush.Dispose(); textBrush = null; }
			if (strikeLineBrush != null) { strikeLineBrush.Dispose(); strikeLineBrush = null; }
			if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
			if (tableTitleFormat != null) { tableTitleFormat.Dispose(); tableTitleFormat = null; }
			if (tableContentFormat != null) { tableContentFormat.Dispose(); tableContentFormat = null; }
			if (tableBorderBrush != null) { tableBorderBrush.Dispose(); tableBorderBrush = null; }
			if (tableBackgroundBrush != null) { tableBackgroundBrush.Dispose(); tableBackgroundBrush = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			Print("OnRender started");

			if (RenderTarget == null || textFormat == null || textBrush == null || strikeLineBrush == null)
			{
				Print("OnRender - Essential resources are null");
				return;
			}

			Print("Calling PlotStrikeLevels");
			PlotStrikeLevels(chartControl, chartScale);

			Print("Calling DrawVolumeTable");
			DrawVolumeTable(chartControl);

			// Draw Expected Move levels
			Print("Calling ExpectedMoveLevels");

			if (expectedMaxPrice > 0 && expectedMinPrice > 0)
			{
				Print("Expected Max Price: " + expectedMaxPrice);
				Print("Expected Min Price: " + expectedMinPrice);
				DrawExpectedMoveLevel(chartControl, chartScale, expectedMaxPrice, "Expected Max Price");
				DrawExpectedMoveLevel(chartControl, chartScale, expectedMinPrice, "Expected Min Price");
			}
			else
			{
				Print("Invalid expected max/min prices. Cannot plot expected move levels");
			}

			Print("OnRender completed");
		}

		private SharpDX.Color GetColorForNetAskVolume(double netAskVolume)
		{
			// Define the range for color interpolation
			double maxVolume = 30000; // Adjust this value based on your typical volume range

			// Normalize the netAskVolume to a value between -1 and 1
			double normalizedVolume = Math.Max(-1, Math.Min(1, netAskVolume / maxVolume));

			byte alpha = 64; // 50% opacity

			if (Math.Abs(normalizedVolume) < 0.1) // Close to zero, use a more vibrant blue
			{
				return new SharpDX.Color((byte)65, (byte)105, (byte)225, alpha); // Royal Blue
			}
			else if (normalizedVolume < 0) // Negative, use red gradient
			{
				byte intensity = (byte)(255 * -normalizedVolume);
				return new SharpDX.Color(intensity, (byte)0, (byte)0, alpha);
			}
			else // Positive, use green gradient
			{
				byte intensity = (byte)(255 * normalizedVolume);
				return new SharpDX.Color((byte)0, intensity, (byte)0, alpha);
			}
		}
		
		private void PlotStrikeLevels(ChartControl chartControl, ChartScale chartScale)
		{
			Print("PlotStrikeLevels started");

			if (strikeLevels.Count == 0 || indexStrikes.Count != strikeLevels.Count || netAskVolumes.Count != strikeLevels.Count) 
			{
				Print("Mismatch in data counts or no strike levels. Returning.");
				return;
			}

			float xStart = ChartPanel.X;
			float xEnd = ChartPanel.X + ChartPanel.W;

			for (int i = 0; i < strikeLevels.Count; i++)
			{
				double strikeLevel = strikeLevels[i];
				double indexStrike = indexStrikes[i];
				double netAskVolume = netAskVolumes[i];
				float y = chartScale.GetYByValue(strikeLevel);

				SharpDX.Color rectangleColor = GetColorForNetAskVolume(netAskVolume);

				// Draw the line
				using (var lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)65, (byte)105, (byte)225, (byte)64))) // Royal Blue with 50% opacity
				{
					RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, 2);
				}

				// Calculate rectangle coordinates
				float yTop = chartScale.GetYByValue(strikeLevel + 1);
				float yBottom = chartScale.GetYByValue(strikeLevel - 1);

				// Draw the rectangle
				using (var rectangleBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, rectangleColor))
				{
					RenderTarget.FillRectangle(
						new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop),
						rectangleBrush
					);
				}

				// Draw text
				string strikeText = "Strike: " + Math.Round(indexStrike).ToString() + " (" + Math.Round(strikeLevel).ToString() + ")";
				string volumeText = "Net Ask Vol: " + FormatVolume(netAskVolume);

				using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White))
				using (var strikeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, strikeText, textFormat, float.MaxValue, float.MaxValue))
				using (var volumeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, volumeText, textFormat, float.MaxValue, float.MaxValue))
				{
					float strikeTextWidth = strikeTextLayout.Metrics.Width;
					float strikeTextHeight = strikeTextLayout.Metrics.Height;
					float volumeTextWidth = volumeTextLayout.Metrics.Width;

					float x = (float)ChartPanel.X + (float)ChartPanel.W - Math.Max(strikeTextWidth, volumeTextWidth) - 5;
					float yStrikeText = y - strikeTextHeight;
					float yVolumeText = y + 2;

					RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yStrikeText), strikeTextLayout, textBrush);
					RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yVolumeText), volumeTextLayout, textBrush);
				}
			}

			Print("PlotStrikeLevels completed");
		}

		private void DrawExpectedMoveLevel(ChartControl chartControl, ChartScale chartScale, double price, string label)
		{
			Print("DrawExpectedMoveLevel started for " + label + " Price: " + price);

			float y = chartScale.GetYByValue(price*ratio); // Added ratio to set the expected move level on the futures chart

			float xStart = ChartPanel.X;
			float xEnd = ChartPanel.X + ChartPanel.W;

			// Draw the line
			using (var lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)255, (byte)0, (byte)64))) // Pale yellow with 50% opacity
			{
				RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, 2);
			}

			// Draw the rectangle
			using (var rectangleBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)255, (byte)0, (byte)32))) // Pale yellow with 25% opacity
			{
				float yTop = chartScale.GetYByValue(price*ratio + 0.25);
				float yBottom = chartScale.GetYByValue(price*ratio - 0.25);
				RenderTarget.FillRectangle(new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop), rectangleBrush);
			}

			// Draw text
			/*string priceText = label + ": " + Math.Round(price, 2).ToString();
			using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White))
			using (var textLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, priceText, textFormat, float.MaxValue, float.MaxValue))
			{
				float textWidth = textLayout.Metrics.Width;
				float x = (float)ChartPanel.X + (float)ChartPanel.W - textWidth - 5;
				float yText = y - textLayout.Metrics.Height;

				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yText), textLayout, textBrush);
			}
			*/
			Print("DrawExpectedMoveLevel completed for " + label);
		}
		private string FormatVolume(double volume)
		{
			// Convert the volume to hundreds
			double volumeInHundreds = volume * 100; 
			string sign = volumeInHundreds < 0 ? "-" : "";
			return sign + "$" + String.Format("{0:N0}", Math.Abs(volumeInHundreds)).Replace(",", ".");
		}
		private void DrawVolumeTable(ChartControl chartControl)
		{
			 Print("DrawVolumeTable started");
			 
			float tableWidth = 300;
			float tableHeight = 130;
			float padding = 10;
			float labelWidth = 120;
			float titleHeight = 30;
			float rowHeight = 30;

			// Use ChartPanel properties for positioning
			float x = ChartPanel.W - tableWidth - padding;
			float y = ChartPanel.H - tableHeight - padding;

			// Draw table background
			RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, tableWidth, tableHeight), tableBackgroundBrush);

			// Draw table border
			RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, tableWidth, tableHeight), tableBorderBrush);

			// Prepare the text to display
			string statusText = null;
			if (!isLoggedIn)
			{
				statusText = "Login failed. Please check your credentials.";
			}
			else if (!isDataFetched)
			{
				statusText = "Failed to fetch options data. Please check your connection.";
			}
			else if (strikeLevels.Count == 0)
			{
				statusText = "No options data available.";
			}

			if (statusText != null)
			{
				// Draw status message instead of normal content
				// Center the title
				tableTitleFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
				RenderTarget.DrawText("Status", tableTitleFormat, new SharpDX.RectangleF(x, y, tableWidth, titleHeight), textBrush);
				RenderTarget.DrawText(statusText, tableContentFormat, new SharpDX.RectangleF(x + 5, y + titleHeight, tableWidth - 10, tableHeight - titleHeight), textBrush);
			}
			else
			{
				// Draw normal volume table content
				// Center the title
				tableTitleFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
				RenderTarget.DrawText("Summary", tableTitleFormat, new SharpDX.RectangleF(x, y, tableWidth, titleHeight), textBrush);

				// Draw table content
				float contentY = y + titleHeight;
				float valueX = x + labelWidth;

				// Underlying Symbol
				RenderTarget.DrawText("Underlying:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY, labelWidth, rowHeight), textBrush);
				RenderTarget.DrawText(underlyingSymbol, tableContentFormat, new SharpDX.RectangleF(valueX, contentY, tableWidth - labelWidth - 5, rowHeight), textBrush);

				// Last Update Time
				RenderTarget.DrawText("Last Update:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 1 * rowHeight, labelWidth, rowHeight), textBrush);
				RenderTarget.DrawText(FormatTimestamp(lastUpdateTimestamp), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 1 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

				// Total Ask Volume
				RenderTarget.DrawText("Total Ask Volume:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 2 * rowHeight, labelWidth, rowHeight), textBrush);
				RenderTarget.DrawText(FormatVolumeForDisplay(totalAskVolume), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 2 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

				// Total GEX Volume
				RenderTarget.DrawText("Total GEX Volume:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 3 * rowHeight, labelWidth, rowHeight), textBrush);
				RenderTarget.DrawText(FormatGexVolumeForDisplay(totalGexVolume), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 3 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

				}

			Print("DrawVolumeTable completed");
		}
		private string FormatVolumeForDisplay(double volume)
		{
			// Convert the volume to hundreds
			double volumeInHundreds = volume * 100; 
			string sign = volumeInHundreds < 0 ? "-" : "";
			return sign + "$" + String.Format("{0:N0}", Math.Abs(volumeInHundreds)).Replace(",", ".");
		}
		private string FormatGexVolumeForDisplay(double gexVolume)
		{
			// GEX is in billions, so divide by 1 billion to get the number of billions
			double gexInBillions = gexVolume / 1000000000;
			string sign = gexInBillions < 0 ? "-" : "";
			return sign + "$" + String.Format("{0:F3}", Math.Abs(gexInBillions)) + " Bn";
		}
		
		private async Task ConnectWebSocket()
		{
			int maxRetries = 3;
			int retryDelay = 5000; // 5 seconds

			for (int i = 0; i < maxRetries; i++)
			{
				if (string.IsNullOrEmpty(bearerToken))
				{
					Print("Bearer token is not set. Cannot connect to WebSocket.");
					return;
				}

				Print("Attempting WebSocket connection (attempt " + (i + 1) + " of " + maxRetries + ")");
				try
				{
					var wsUri = new Uri(WebSocketUrl + "?token=" + bearerToken);
					await webSocket.ConnectAsync(wsUri, cts.Token);
					Print("WebSocket connection established");
					Task.Run(() => ReceiveWebSocketMessages());
					return;
				}
				catch (Exception ex)
				{
					Print("WebSocket connection attempt " + (i + 1) + " failed: " + ex.Message);
				}

				if (i < maxRetries - 1)
				{
					Print("Retrying in " + (retryDelay / 1000) + " seconds...");
					await Task.Delay(retryDelay);
				}
				else
				{
					Print("Max retries reached. " + "WebSocket connection failed.");
				}
			}
		}

		private async Task ReceiveWebSocketMessages()
		{
			var buffer = new byte[1024 * 4];
			while (webSocket.State == WebSocketState.Open)
			{
				WebSocketReceiveResult result;
				using (var ms = new MemoryStream())
				{
					do
					{
						result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
						ms.Write(buffer, 0, result.Count);
					}
					while (!result.EndOfMessage);

					ms.Seek(0, SeekOrigin.Begin);
					using (var reader = new StreamReader(ms, Encoding.UTF8))
					{
						string message = await reader.ReadToEndAsync();
						ProcessWebSocketMessage(message);
					}
				}

				if (result.MessageType == WebSocketMessageType.Close)
				{
					await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
					Print("WebSocket connection closed");
					return;
				}
			}
		}

		private void ProcessWebSocketMessage(string message)
		{
			try
			{
				var data = jsonSerializer.Deserialize<Dictionary<string, object>>(message);
				if (data.ContainsKey("type") && data["type"].ToString() == "chunk")
				{
					int chunkIndex = Convert.ToInt32(data["chunkIndex"]);
					int totalChunks = Convert.ToInt32(data["totalChunks"]);
					string chunkData = data["data"].ToString();

					// Ensure the messageChunks list has enough capacity
					while (messageChunks.Count <= chunkIndex)
					{
						messageChunks.Add(null);
					}

					messageChunks[chunkIndex] = chunkData;

					if (messageChunks.Count(chunk => chunk != null) == totalChunks)
					{
						string fullMessage = string.Join("", messageChunks);
						messageChunks.Clear(); // Reset for next message

						ProcessReassembledMessage(fullMessage);
					}
				}
				else
				{
					// Handle other message types if any
					Print("Received non-chunk message: " + message);
					ProcessReassembledMessage(message);
				}
			}
			catch (Exception ex)
			{
				Print("Error processing WebSocket message: " + ex.Message);
			}
		}

		private void ProcessReassembledMessage(string fullMessage)
		{
			try
			{
				var data = jsonSerializer.Deserialize<Dictionary<string, object>>(fullMessage);
				if (data.ContainsKey("type") && data["type"].ToString() == "update")
				{
					Print("Received new options chain data");
					if (data.ContainsKey("data"))
					{
						var optionsData = jsonSerializer.Serialize(data["data"]);
						ProcessOptionsData(optionsData);
					}
				}
			}
			catch (Exception ex)
			{
				Print("Error processing reassembled message: " + ex.Message);
			}
		}

		private void DisconnectWebSocket()
		{
			cts.Cancel();
			if (webSocket.State == WebSocketState.Open)
			{
				webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).Wait();
			}
			webSocket.Dispose();
			cts.Dispose();
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name="Username", Description="User Name for KriyaFX service", Order=1, GroupName="Parameters")]
		public string Username
		{ get; set; }

		[NinjaScriptProperty]
		[Display(Name="Password", Description="Password for KriyaFX Service", Order=2, GroupName="Parameters")]
		public string Password
		{ get; set; }

		[NinjaScriptProperty]
		[Display(Name="WebSocket URL", Description="URL for the WebSocket server", Order=3, GroupName="Parameters")]
		public string WebSocketUrl { get; set; }
		#endregion

	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private KriyaFXOptionsMap[] cacheKriyaFXOptionsMap;
		public KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password)
		{
			return KriyaFXOptionsMap(Input, username, password);
		}

		public KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input, string username, string password)
		{
			if (cacheKriyaFXOptionsMap != null)
				for (int idx = 0; idx < cacheKriyaFXOptionsMap.Length; idx++)
					if (cacheKriyaFXOptionsMap[idx] != null && cacheKriyaFXOptionsMap[idx].Username == username && cacheKriyaFXOptionsMap[idx].Password == password && cacheKriyaFXOptionsMap[idx].EqualsInput(input))
						return cacheKriyaFXOptionsMap[idx];
			return CacheIndicator<KriyaFXOptionsMap>(new KriyaFXOptionsMap(){ Username = username, Password = password }, input, ref cacheKriyaFXOptionsMap);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password)
		{
			return indicator.KriyaFXOptionsMap(Input, username, password);
		}

		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input , string username, string password)
		{
			return indicator.KriyaFXOptionsMap(input, username, password);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password)
		{
			return indicator.KriyaFXOptionsMap(Input, username, password);
		}

		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input , string username, string password)
		{
			return indicator.KriyaFXOptionsMap(input, username, password);
		}
	}
}

#endregion
#endregion