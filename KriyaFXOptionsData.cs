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
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
	public class KriyaFXOptionsData : Indicator
	{
		private HttpClient httpClient;
		private string bearerToken;
		private JavaScriptSerializer jsonSerializer;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Retrieves and logs Options Data from the KriyaFX service";
				Name										= "KriyaFXOptionsData";
				Calculate									= Calculate.OnBarClose;
				IsOverlay									= false;
				DisplayInDataBox							= true;
				DrawOnPricePanel							= true;
				DrawHorizontalGridLines						= true;
				DrawVerticalGridLines						= true;
				PaintPriceMarkers							= true;
				ScaleJustification							= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive					= true;
				Username					= string.Empty;
				Password					= string.Empty;
			}
			else if (State == State.Configure)
			{
				httpClient = new HttpClient();
				jsonSerializer = new JavaScriptSerializer();
			}
			else if (State == State.DataLoaded)
			{
				// Login and fetch data when the indicator is loaded
				Login();
			}
			else if (State == State.Terminated)
			{
				if (httpClient != null)
					httpClient.Dispose();
			}
		}

		protected override void OnBarUpdate()
		{
			// This method is called on each bar update
			// You can add logic here if needed
		}

		private async void Login()
		{
			try
			{
				var loginData = new
				{
					username = Username,
					password = Password
				};

				Print("Attempting login with username: " + Username);

				var jsonContent = jsonSerializer.Serialize(loginData);
				Print("JSON payload: " + jsonContent);

				var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
				var request = new HttpRequestMessage(HttpMethod.Post, "https://kriyafx.de/api/login")
				{
					Content = content
				};

				// Log request details
				Print("Request URL: " + request.RequestUri);
				Print("Request Method: " + request.Method);
				foreach (var header in request.Headers)
				{
					Print("Request Header: " + header.Key + ": " + string.Join(", ", header.Value));
				}

				var response = await httpClient.SendAsync(request);

				Print("Response Status Code: " + response.StatusCode);

				if (response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync();
					Print("Response Content: " + responseContent);

					var tokenObject = jsonSerializer.Deserialize<Dictionary<string, object>>(responseContent);
					bearerToken = tokenObject["token"].ToString();
					Print("Login successful. Bearer token received.");

					FetchOptionsData();
				}
				else
				{
					var responseContent = await response.Content.ReadAsStringAsync();
					Print("Login failed. Status code: " + response.StatusCode);
					Print("Response Content: " + responseContent);

					// Log response headers
					foreach (var header in response.Headers)
					{
						Print("Response Header: " + header.Key + ": " + string.Join(", ", header.Value));
					}
				}
			}
			catch (Exception ex)
			{
				Print("Error during login: " + ex.Message);
				Print("Stack Trace: " + ex.StackTrace);
			}
		}

		private async void FetchOptionsData()
		{
			try
			{
				var request = new HttpRequestMessage(HttpMethod.Get, "https://kriyafx.de/api/options-chain?latest");
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

				var response = await httpClient.SendAsync(request);

				if (response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync();
					Print("Options Data:");
					Print(responseContent);

					// You can add further processing of the options data here if needed
				}
				else
				{
					Print("Failed to fetch options data. Status code: " + response.StatusCode);
				}
			}
			catch (Exception ex)
			{
				Print("Error fetching options data: " + ex.Message);
			}
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