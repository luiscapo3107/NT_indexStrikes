# KriyaFX NinjaTrader Indicators

## Overview
A collection of NinjaTrader 8 indicators for advanced options and futures trading visualization, powered by KriyaFX data services:

- **KriyaFXOptionsMap**: Comprehensive options data visualization with volume analysis
- **KriyaFXIndexStrikes**: Strike level plotting with real-time market data integration
- **KriyaFXGEX**: Gamma exposure visualization and analysis

## Features

### KriyaFXOptionsMap
- Real-time options data visualization
- Volume analysis with customizable thresholds
- Probability of Touch (PoT) indicators
- Expected move levels
- Maximum Gamma Exposure (GEX) levels
- Customizable visual settings

### KriyaFXIndexStrikes
- Strike level visualization
- Real-time futures price tracking
- Ratio calculations with moving averages
- WebSocket integration for live data
- Royal blue highlighting for better visibility

### KriyaFXGEX
- Gamma exposure analysis
- Volume-based strike level monitoring
- Real-time data updates
- Customizable money thresholds
- Summary table with key metrics

## Requirements
- NinjaTrader 8
- Active internet connection
- KriyaFX account credentials
- Supported futures/options data feed

## Installation
1. Download the indicator files:
   - `KriyaFXOptionsMap.cs`
   - `KriyaFXIndexStrikes.cs`
   - `KriyaFXGEX.cs`
2. Place files in your NinjaTrader custom indicators folder:
   - Default path: `Documents/NinjaTrader 8/bin/Custom/Indicators`
3. In NinjaTrader:
   - Tools > Options > NinjaScript > Compile
   - Restart NinjaTrader if prompted

## Configuration

### Common Settings
- **Username**: Your KriyaFX account username
- **Password**: Your KriyaFX account password
- **WebSocket URL**: KriyaFX WebSocket endpoint (default: ws://kriyafx.de)

### Specific Settings

#### KriyaFXOptionsMap
- Strike Money Threshold ($M)
- Net Difference Threshold ($M)
- Show Probability of Touch
- Show GEX Levels
- Show Expected Move Levels
- Selected Symbol (SPX/SPY)

#### KriyaFXIndexStrikes
- Selected Symbol
- Display formatting options

#### KriyaFXGEX
- Strike Money Threshold ($M)
- Net Difference Threshold ($M)

## Usage
1. Open a NinjaTrader chart
2. Right-click > Indicators
3. Select desired indicator:
   - KriyaFXOptionsMap
   - KriyaFXIndexStrikes
   - KriyaFXGEX
4. Configure settings in the indicator properties dialog
5. Ensure Panel view is set to current Panel for proper text display

## Data Updates
- All indicators connect via WebSocket for real-time updates
- Data is processed and displayed automatically
- Connection status and timestamps are shown in the interface
- Automatic reconnection on connection loss

## Support
For technical support or questions:
1. Visit KriyaFX website
2. Contact support team
3. Check documentation

## License
[Your License Information Here]

## Disclaimer
This software is for informational purposes only. Trading futures and options involves substantial risk of loss and is not suitable for all investors.

