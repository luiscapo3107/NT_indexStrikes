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
using System.Timers;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
	public class KriyaFXOptionsData : Indicator
	{
		private HttpClient httpClient;
		private string bearerToken;
		private JavaScriptSerializer jsonSerializer;
		private Timer updateTimer;
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

		private bool strikeLevelsCalculated = false;
		private DateTime marketOpenTime = new DateTime(1, 1, 1, 15, 30, 0); // 15:30 CET

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Retrieves and plots Options Data from the KriyaFX service";
				Name										= "KriyaFXOptionsData";
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
			}
			else if (State == State.Configure)
			{
				httpClient = new HttpClient();
				jsonSerializer = new JavaScriptSerializer();
				updateTimer = new Timer(10000); // 10 seconds
				updateTimer.Elapsed += OnTimerElapsed;
				updateTimer.AutoReset = true;
			}
			else if (State == State.DataLoaded)
			{
				updateTimer.Start();
				Login();
			}
			else if (State == State.Terminated)
			{
				if (httpClient != null)
					httpClient.Dispose();
				if (updateTimer != null)
				{
					updateTimer.Stop();
					updateTimer.Dispose();
				}
				DisposeSharpDXResources();
			}
		}

		protected override void OnBarUpdate() { }

		private async void Login()
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

					FetchOptionsData();
				}
				else
				{
					Print("Login failed. Status code: " + response.StatusCode);
				}
			}
			catch (Exception ex)
			{
				Print("Error during login: " + ex.Message);
			}
		}

		private async void FetchOptionsData()
		{
			try
			{
				Print("Fetching options data...");
				var request = new HttpRequestMessage(HttpMethod.Get, "https://kriyafx.de/api/options-chain?latest");
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

				var response = await httpClient.SendAsync(request);
				Print("Response status code: " + response.StatusCode);

				if (response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync();
					Print("Response content length: " + responseContent.Length);
					Print("Response content (first 500 chars): " + responseContent.Substring(0, Math.Min(500, responseContent.Length)));
					ProcessOptionsData(responseContent);
				}
				else
				{
					Print("Failed to fetch options data. Status code: " + response.StatusCode);
					Print("Response content: " + await response.Content.ReadAsStringAsync());
				}
			}
			catch (Exception ex)
			{
				Print("Error fetching options data: " + ex.Message);
				Print("Stack trace: " + ex.StackTrace);
			}
		}
		private void ProcessOptionsData(string json)
		{
			try
			{
				Print("Processing options data...");
				var data = jsonSerializer.Deserialize<Dictionary<string, object>>(json);
				Print("Deserialized data keys: " + string.Join(", ", data.Keys));

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

				Print("Processed strike levels count: " + strikeLevels.Count);

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
				Print("Stack trace: " + ex.StackTrace);
			}
		}
		private async void OnTimerElapsed(object sender, ElapsedEventArgs e)
		{
			if (!strikeLevelsCalculated && DateTime.Now.TimeOfDay >= marketOpenTime.TimeOfDay)
			{
				await CalculateStrikeLevels();
			}
			else if (strikeLevelsCalculated)
			{
				await UpdateNetAskVolumes();
			}
		}

		private async Task CalculateStrikeLevels()
		{
			try
			{
				Print("Calculating strike levels...");
				var optionsData = await FetchLatestOptionsData();
				if (optionsData != null)
				{
					ProcessStrikeLevels(optionsData);
					strikeLevelsCalculated = true;
					Print("Strike levels calculated successfully.");
				}
			}
			catch (Exception ex)
			{
				Print("Error calculating strike levels: " + ex.Message);
			}
		}

		private async Task UpdateNetAskVolumes()
		{
			try
			{
				Print("Updating Net Ask Volumes...");
				var optionsData = await FetchLatestOptionsData();
				if (optionsData != null)
				{
					UpdateVolumes(optionsData);
					Print("Net Ask Volumes updated successfully.");
				}
			}
			catch (Exception ex)
			{
				Print("Error updating Net Ask Volumes: " + ex.Message);
			}
		}

		private async Task<Dictionary<string, object>> FetchLatestOptionsData()
		{
			Print("Fetching latest options data...");
			var request = new HttpRequestMessage(HttpMethod.Get, "https://kriyafx.de/api/options-chain?latest");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

			var response = await httpClient.SendAsync(request);
			Print("Response status code: " + response.StatusCode);

			if (response.IsSuccessStatusCode)
			{
				var responseContent = await response.Content.ReadAsStringAsync();
				Print("Response content length: " + responseContent.Length);
				return jsonSerializer.Deserialize<Dictionary<string, object>>(responseContent);
			}
			else
			{
				Print("Failed to fetch options data. Status code: " + response.StatusCode);
				return null;
			}
		}

		private void ProcessStrikeLevels(Dictionary<string, object> data)
		{
			// ... existing code to process strike levels ...
			// Make sure to clear and populate strikeLevels and indexStrikes lists
			// but don't update netAskVolumes here
		}

		private void UpdateVolumes(Dictionary<string, object> data)
		{
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

			var dataJson = jsonSerializer.Serialize(options["Data"]);
			var optionsData = jsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataJson);

			netAskVolumes.Clear();

			foreach (var item in optionsData)
			{
				if (item.ContainsKey("strike") && item.ContainsKey("Net_ASK_Volume"))
				{
					double indexStrike = Convert.ToDouble(item["strike"]);
					double netAskVolume = Convert.ToDouble(item["Net_ASK_Volume"]);

					int index = indexStrikes.IndexOf(indexStrike);
					if (index != -1)
					{
						netAskVolumes.Add(netAskVolume);
					}
				}
			}

			// Invalidate the chart to trigger OnRender
			Dispatcher.InvokeAsync(() =>
			{
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

		public override void OnRenderTargetChanged()
		{
			// Dispose existing resources
			DisposeSharpDXResources();

			if (RenderTarget != null)
			{
				// Initialize SharpDX resources
				textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
				textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
				strikeLineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.DodgerBlue);
			}
		}

		private void DisposeSharpDXResources()
		{
			if (textBrush != null) { textBrush.Dispose(); textBrush = null; }
			if (strikeLineBrush != null) { strikeLineBrush.Dispose(); strikeLineBrush = null; }
			if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			Print("OnRender called");

			lock (renderLock)
			{
				if (RenderTarget == null)
				{
					Print("RenderTarget is null");
					return;
				}

				if (textFormat == null || textBrush == null || strikeLineBrush == null)
				{
					Print("Some SharpDX resources are null");
					return;
				}

				Print("Calling PlotStrikeLevels");
				PlotStrikeLevels(chartControl, chartScale);
			}
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
			Print("PlotStrikeLevels called. Strike levels count: " + strikeLevels.Count);

			if (strikeLevels.Count == 0 || indexStrikes.Count != strikeLevels.Count || netAskVolumes.Count != strikeLevels.Count) return;

			float xStart = ChartPanel.X;
			float xEnd = ChartPanel.X + ChartPanel.W;

			for (int i = 0; i < strikeLevels.Count; i++)
			{
				double strikeLevel = strikeLevels[i];
				double indexStrike = indexStrikes[i];
				double netAskVolume = netAskVolumes[i];
				float y = chartScale.GetYByValue(strikeLevel);

				Print("Plotting strike level: " + strikeLevel + ", Index strike: " + indexStrike + ", Y coordinate: " + y + ", Net Ask Volume: " + netAskVolume);

				SharpDX.Color rectangleColor = GetColorForNetAskVolume(netAskVolume);

				// Draw the line
				using (var lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)65, (byte)105, (byte)225, (byte)64))) // Royal Blue with 50% opacity
				{
					RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, 1);
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

		private string FormatVolume(double volume)
		{
			// Convert the volume to hundreds
			double volumeInHundreds = volume * 100; 
			string sign = volumeInHundreds < 0 ? "-" : "";
			return sign + "$" + String.Format("{0:N0}", Math.Abs(volumeInHundreds)).Replace(",", ".");
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
		#endregion

	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private KriyaFXOptionsData[] cacheKriyaFXOptionsData;
		public KriyaFXOptionsData KriyaFXOptionsData(string username, string password)
		{
			return KriyaFXOptionsData(Input, username, password);
		}

		public KriyaFXOptionsData KriyaFXOptionsData(ISeries<double> input, string username, string password)
		{
			if (cacheKriyaFXOptionsData != null)
				for (int idx = 0; idx < cacheKriyaFXOptionsData.Length; idx++)
					if (cacheKriyaFXOptionsData[idx] != null && cacheKriyaFXOptionsData[idx].Username == username && cacheKriyaFXOptionsData[idx].Password == password && cacheKriyaFXOptionsData[idx].EqualsInput(input))
						return cacheKriyaFXOptionsData[idx];
			return CacheIndicator<KriyaFXOptionsData>(new KriyaFXOptionsData(){ Username = username, Password = password }, input, ref cacheKriyaFXOptionsData);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.KriyaFXOptionsData KriyaFXOptionsData(string username, string password)
		{
			return indicator.KriyaFXOptionsData(Input, username, password);
		}

		public Indicators.KriyaFXOptionsData KriyaFXOptionsData(ISeries<double> input , string username, string password)
		{
			return indicator.KriyaFXOptionsData(input, username, password);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.KriyaFXOptionsData KriyaFXOptionsData(string username, string password)
		{
			return indicator.KriyaFXOptionsData(Input, username, password);
		}

		public Indicators.KriyaFXOptionsData KriyaFXOptionsData(ISeries<double> input , string username, string password)
		{
			return indicator.KriyaFXOptionsData(input, username, password);
		}
	}
}

#endregion
