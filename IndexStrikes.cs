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
using System.Threading.Tasks; 
using System.Windows.Threading; 
using System.Web.Script.Serialization; //For JSON Parsing
using SharpDX;
using SharpDX.Direct2D1; 
using SharpDX.DirectWrite; 
using Brushes = System.Windows.Media.Brushes; 
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
    public class IndexStrikes : Indicator
    {
        private HttpClient httpClient;
        private JavaScriptSerializer jsonSerializer;
        private const int STRIKES_ABOVE = 10; 
        private const int STRIKES_BELOW = 10; 
        private List<double> strikeLevels = new List<double>(); 
        private List<double> plotted_strikes = new List<double>();
        private bool isInitialized = false;
        private List<double> indexStrikes = new List<double>();
        private string Ticker = ""; 

        // Add these private variables
		private System.Windows.Media.Brush areaBrush;
		private System.Windows.Media.Brush outlineBrush;


        protected override void OnStateChange()
        { 
            if (State == State.SetDefaults)
            {
                Description                                 = @"Plots the strike levels of the given index in the chart.";
                Name                                        = "IndexStrikes";
                Calculate                                   = Calculate.OnBarClose;
                IsOverlay                                   = false;
                DisplayInDataBox                            = true;
                DrawOnPricePanel                            = true;
                DrawHorizontalGridLines                     = true;
                DrawVerticalGridLines                       = true;
                PaintPriceMarkers                           = true;
                ScaleJustification                          = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                //Disable this property if your indicator requires custom values that cumulate with each new market data event. 
                //See Help Guide for additional information.
                IsSuspendedWhileInactive                    = true;
                APIToken                    = string.Empty;
                IndexTicker                 = string.Empty;
            }
            else if (State == State.Configure)
            {
                Print("Initializing httpClient and jsonSerializer...");
                httpClient = new HttpClient(); 
                jsonSerializer = new JavaScriptSerializer(); 
                Print("Initialization complete.");

              	// Initialize brushes
				areaBrush = System.Windows.Media.Brushes.CornflowerBlue.Clone();
				areaBrush.Opacity = 0.04;

				outlineBrush = System.Windows.Media.Brushes.CornflowerBlue.Clone();
				outlineBrush.Opacity = 1.0;

            }
            else if (State == State.Realtime)
            {
                if(!isInitialized){
                    isInitialized = true; 
                    Ticker = IndexTicker; 
                    Print("Calling Check MarketStatusAsync."); 
                    // Start the async operation without blocking
                    CheckMarketStatusAsync(); 
                }
            }
            else if (State == State.Terminated)
            {
                if(httpClient != null)
                {
                    Print("Disposing httpClient...");
                    httpClient.Dispose(); 
                    Print("httpClient disposed.");
                }

                // Dispose of brushes
                if (areaBrush != null)
                {
          
                    areaBrush = null;
                }

                if (outlineBrush != null)
                {
              
                    outlineBrush = null;
                }
            }   
        }

        protected override void OnBarUpdate()
        {

        }

        private async Task CheckMarketStatusAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(APIToken))
                {
                    Print("API Token is not set.");
                    return;
                }

                string marketStatusUrl = "https://api.marketdata.app/v1/markets/status" + "?" + "token="+ APIToken;
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, marketStatusUrl);
                HttpResponseMessage response = await httpClient.SendAsync(request);

                string content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    //bool isMarketOpen = ParseMarketStatus(content); //We fake it now for testing

                    bool isMarketOpen = true; // Faked value for testing while the market is closed

                    Print("Market Status: "+isMarketOpen);

                    if (isMarketOpen)
                    {
                        await GetIndexPriceAsync();
                    }
                    else
                    {
                        UpdateMarketStatusOnUI(false);
                    }
                }
                else
                {
                    Print("Error checking market status: " + response.ReasonPhrase);
                }
            }
            catch (Exception ex)
            {
                Print("Exception in CheckMarketStatusAsync: "+ ex.Message);
            }
        }

        private async Task GetIndexPriceAsync()
        {
            try
            {
                string indexPriceUrl = "https://api.marketdata.app/v1/stocks/quotes/" + IndexTicker + "?" + "token="+ APIToken;
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, indexPriceUrl);

                HttpResponseMessage response = await httpClient.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    double indexPrice = ParseIndexPrice(content);
                    double futurePrice = GetCurrentAsk() - TickSize; 
                    Print("IndexPrice: "+indexPrice); 
                    Print("FuturePrice: "+futurePrice); 

                    // Update UI on the UI thread
                    Dispatcher.InvokeAsync(() =>
                    {
                        UpdateStrikeLevelsOnUI(indexPrice, futurePrice);
                    });
                }
                else
                {
                    Print("Error retrieving index price: "+ response.ReasonPhrase);
                }
            }
            catch (Exception ex)
            {
                Print("Exception in GetIndexPriceAsync: "+ ex.Message);
            }
        }

        // ... (Other methods remain unchanged) ...
		private void UpdateStrikeLevelsOnUI(double indexPrice,double futurePrice)
		{
			Print("Calling UpdateStrikeLevelsOnUI"); 
			try{
				
				        // Check if there are any bars loaded
		        if (Count < 1)
		        {
		            Print("No bars loaded. Exiting UpdateStrikeLevelsOnUI.");
		            return;
		        }
				
				//Clear previous strike levels
				strikeLevels.Clear(); 
				indexStrikes.Clear(); 
				
				double ratio = futurePrice/indexPrice;
				double roundedIndexStrike = Math.Floor(indexPrice); 
				
				// Draw new lines
				for (int i = -STRIKES_BELOW; i <= STRIKES_ABOVE; i++)
	        	{
	                double indexStrike = (roundedIndexStrike + i);
	                double strikeLevel = indexStrike * ratio;
					strikeLevels.Add(strikeLevel); 
					indexStrikes.Add(indexStrike);
					
	                string lineTag = IndexTicker + ": Line " + indexStrike.ToString();
					
	                Draw.HorizontalLine(this, lineTag, strikeLevel, Brushes.DodgerBlue, DashStyleHelper.Solid, 1);

					// Draw rectangle around the strike level
            		string rectangleTag = IndexTicker + ": RegionHighlight " + indexStrike.ToString();

					// Set area opacity to 4% (areaOpacity ranges from 0 to 100)
           			int areaOpacity = 4;
					
					Draw.RegionHighlightY(
	                this,
	                rectangleTag,
					false,
	                strikeLevel - 2,
	                strikeLevel + 2,
	                null, //no outlineBrush
					areaBrush,
	                areaOpacity
	           		);

					// Force the chart to redraw
        			if (ChartControl != null)
            		ChartControl.InvalidateVisual();
	        	}
				Print("Strike lines and region plotted"); 
			}catch (Exception ex)
			{	
				Print("Exception in UpdatesStrikeLevelsOnUI: "+ ex.Message); 
			}
			
			
		}


        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            Print("Plotting Strike text in OnRender Method"); 
            // Ensure we have strike levels to draw
            if (strikeLevels == null || strikeLevels.Count == 0)
                return;

            // Create the SharpDX resources
            SharpDX.Direct2D1.RenderTarget renderTarget = RenderTarget;
            SharpDX.DirectWrite.TextFormat textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
            SharpDX.Direct2D1.Brush textBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, SharpDX.Color.White);

            // Ensure SharpDX resources are available
            if (textFormat == null || textBrush == null)
                return;

            // Ensure indexStrikes list has the same count as strikeLevels
            if (indexStrikes == null || indexStrikes.Count != strikeLevels.Count)
                return;

            for (int i = 0; i < strikeLevels.Count; i++)
            {
                double strikeLevel = strikeLevels[i];
                double indexStrike = indexStrikes[i];

                // Convert the price (Y-axis value) to a pixel coordinate
                float y = chartScale.GetYByValue(strikeLevel);

                // Adjust Y-coordinate to be 2 pixels above the strike level
                y -= 10;

                // Define the text to display
                string text = "Strike: "+ indexStrike;

                // Create a TextLayout to measure the text size
                using (var textLayout = new TextLayout(Core.Globals.DirectWriteFactory, text, textFormat, float.MaxValue, float.MaxValue))
                {
                    // Get the width and height of the text
                    float textWidth = textLayout.Metrics.Width;
                    float textHeight = textLayout.Metrics.Height;

                    // Calculate X-coordinate: right edge minus text width minus padding
                    float x = (float)ChartPanel.X + (float)ChartPanel.W - textWidth - 10; // 10 pixels padding from the right edge

                    // Adjust Y-coordinate to center the text vertically at the adjusted y
                    y -= textHeight / 2;

                    // Draw the text
                    RenderTarget.DrawTextLayout(new Vector2(x, y), textLayout, textBrush);
                }
            }

            // Dispose of SharpDX resources
            textBrush.Dispose();
            textFormat.Dispose();
            Print("Strike text plotted"); 
        }
		
		private void UpdateMarketStatusOnUI(bool isMarketOpen)
		{
		    // Update the UI to reflect the market status
		    if (!isMarketOpen)
		    {
		        // Clear any existing strike levels
		        strikeLevels.Clear();
		        indexStrikes.Clear();

		        // Remove any drawn objects from the chart
		        RemoveDrawObjects();

		        // Optionally, display a message on the chart
		        Draw.TextFixed(this, "MarketClosed", "Market is closed", TextPosition.Center, Brushes.Red, new Gui.Tools.SimpleFont("Arial", 24), Brushes.Transparent, Brushes.Transparent, 0);
		    }
		}
		
		private double ParseIndexPrice(string json)
		{
		    // Deserialize the JSON into a dynamic object
            var obj = jsonSerializer.Deserialize<dynamic>(json);

		    // Check if the response status is "ok"
		    if ((string)obj["s"] != "ok")
		    {
		        throw new Exception("API response indicates failure.");
		    }

		    // Access the "last" array and get the first element
		    var lastArray = obj["last"] as object[];
		    if (lastArray == null || lastArray.Length == 0)
		    {
		        throw new Exception("No price data available.");
		    }

		    double indexPrice = Convert.ToDouble(lastArray[0]);
		    return indexPrice;
		}



        #region Properties
        [NinjaScriptProperty]
        [Display(Name="APIToken", Description="MarketData.app API token", Order=1, GroupName="Parameters")]
        public string APIToken
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name="IndexTicker", Description="Index Ticket Symbol", Order=2, GroupName="Parameters")]
        public string IndexTicker
        { get; set; }
        #endregion

    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private IndexStrikes[] cacheIndexStrikes;
		public IndexStrikes IndexStrikes(string aPIToken, string indexTicker)
		{
			return IndexStrikes(Input, aPIToken, indexTicker);
		}

		public IndexStrikes IndexStrikes(ISeries<double> input, string aPIToken, string indexTicker)
		{
			if (cacheIndexStrikes != null)
				for (int idx = 0; idx < cacheIndexStrikes.Length; idx++)
					if (cacheIndexStrikes[idx] != null && cacheIndexStrikes[idx].APIToken == aPIToken && cacheIndexStrikes[idx].IndexTicker == indexTicker && cacheIndexStrikes[idx].EqualsInput(input))
						return cacheIndexStrikes[idx];
			return CacheIndicator<IndexStrikes>(new IndexStrikes(){ APIToken = aPIToken, IndexTicker = indexTicker }, input, ref cacheIndexStrikes);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.IndexStrikes IndexStrikes(string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikes(Input, aPIToken, indexTicker);
		}

		public Indicators.IndexStrikes IndexStrikes(ISeries<double> input , string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikes(input, aPIToken, indexTicker);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.IndexStrikes IndexStrikes(string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikes(Input, aPIToken, indexTicker);
		}

		public Indicators.IndexStrikes IndexStrikes(ISeries<double> input , string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikes(input, aPIToken, indexTicker);
		}
	}
}

#endregion
