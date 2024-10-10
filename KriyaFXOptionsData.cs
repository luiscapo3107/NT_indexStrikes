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
				IsSuspendedWhileInactive					= true;
				Username				= string.Empty;
				Password					= string.Empty;
			}
			else if (State == State.Configure)
			{
				httpClient = new HttpClient();
				jsonSerializer = new JavaScriptSerializer();
				updateTimer = new Timer(60000); // 60 seconds
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

				// Calculate future price and ratio
				futurePrice = GetCurrentAsk() - TickSize;
				indexPrice = Convert.ToDouble(data["Price"]);
				ratio = futurePrice / indexPrice;

				Print("Future Price: " + futurePrice);
				Print("Index Price: " + indexPrice);
				Print("Index to Futures Ratio: " + ratio);

				foreach (var item in optionsData)
				{
					if (item.ContainsKey("strike"))
					{
						double strike = Convert.ToDouble(item["strike"]);
						double adjustedStrike = strike * ratio;
						strikeLevels.Add(adjustedStrike);
						Print("Added adjusted strike level: " + adjustedStrike + " (original: " + strike + ")");
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
		private void OnTimerElapsed(object sender, ElapsedEventArgs e)
		{
			FetchOptionsData();
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

		private void PlotStrikeLevels(ChartControl chartControl, ChartScale chartScale)
		{
			Print("PlotStrikeLevels called. Strike levels count: " + strikeLevels.Count);

			if (strikeLevels.Count == 0) return;

			float xStart = ChartPanel.X;
			float xEnd = ChartPanel.X + ChartPanel.W;

			SharpDX.Color paleBlueColor = new SharpDX.Color(173, 216, 230, 200);
			SharpDX.Color transparentBlueColor = new SharpDX.Color(0, 191, 255, 10);

			using (var paleLineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, paleBlueColor))
			using (var transparentAreaBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, transparentBlueColor))
			{
				foreach (double strikeLevel in strikeLevels)
				{
					float y = chartScale.GetYByValue(strikeLevel);

					Print("Plotting strike level: " + strikeLevel + ", Y coordinate: " + y);

					// Draw the line even if it's outside the visible area
					RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), paleLineBrush, 1);

					// Calculate rectangle coordinates
					float yTop = chartScale.GetYByValue(strikeLevel + 2);
					float yBottom = chartScale.GetYByValue(strikeLevel - 2);

					// Draw the rectangle
					RenderTarget.FillRectangle(
						new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop),
						transparentAreaBrush
					);

					string text = strikeLevel.ToString("F2");

					using (var textLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, text, textFormat, float.MaxValue, float.MaxValue))
					{
						float textWidth = textLayout.Metrics.Width;
						float textHeight = textLayout.Metrics.Height;

						float x = (float)ChartPanel.X + (float)ChartPanel.W - textWidth - 5;
						float yText = y - textHeight / 2;

						// Draw text even if it's outside the visible area
						RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yText), textLayout, textBrush);
					}
				}
			}

			Print("PlotStrikeLevels completed");
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