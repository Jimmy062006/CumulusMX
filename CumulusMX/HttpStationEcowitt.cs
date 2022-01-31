﻿using System;
using Unosquare.Labs.EmbedIO;
using System.IO;
using System.Web;
using System.Globalization;
using System.Collections.Specialized;
using System.Reflection;
using ServiceStack.Text;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace CumulusMX
{

	class HttpStationEcowitt : WeatherStation
	{
		private readonly WeatherStation station;
		private bool starting = true;
		private bool stopping = false;
		private readonly NumberFormatInfo invNum = CultureInfo.InvariantCulture.NumberFormat;
		private bool reportStationType = true;
		private EcowittApi api;
		private int maxArchiveRuns = 1;


		public HttpStationEcowitt(Cumulus cumulus, WeatherStation station = null) : base(cumulus)
		{
			this.station = station;

			if (station == null)
			{
				cumulus.LogMessage("Creating HTTP Station (Ecowitt)");
			}
			else
			{
				cumulus.LogMessage("Creating Extra Sensors - HTTP Station (Ecowitt)");
			}

			// Do not set these if we are only using extra sensors
			if (station == null)
			{
				// does not provide 10 min average wind speeds
				cumulus.StationOptions.UseWind10MinAvg = true;

				// does not send DP, so force MX to calculate it
				cumulus.StationOptions.CalculatedDP = true;
				// Same for Wind Chill
				cumulus.StationOptions.CalculatedWC = true;
				// does not provide a forecast, force MX to provide it
				cumulus.UseCumulusForecast = true;
				// does not provide pressure trend strings
				cumulus.StationOptions.UseCumulusPresstrendstr = true;
			}

			if (station == null || (station != null && cumulus.EcowittExtraUseAQI))
			{
				cumulus.Units.AirQualityUnitText = "µg/m³";
			}
			if (station == null || (station != null && cumulus.EcowittExtraUseSoilMoist))
			{
				cumulus.Units.SoilMoistureUnitText = "%";
			}
			if (station == null || (station != null && cumulus.EcowittExtraUseSoilMoist))
			{
				cumulus.Units.LeafWetnessUnitText = "%";
			}



			// Only perform the Start-up if we are a proper station, not a Extra Sensor
			if (station == null)
			{
				Task.Run(getAndProcessHistoryData);
			}
			else
			{
				cumulus.LogMessage("Extra Sensors - HTTP Station (Ecowitt) - Waiting for data...");
			}
		}

		public override void Start()
		{
			if (station == null)
			{
				cumulus.LogMessage("Starting HTTP Station (Ecowitt)");
				DoDayResetIfNeeded();
				cumulus.StartTimersAndSensors();
			}
			else
			{
				cumulus.LogMessage("Starting Extra Sensors - HTTP Station (Ecowitt)");
			}
			starting = false;
		}

		public override void Stop()
		{
			stopping = true;
			if (station == null)
			{
				StopMinuteTimer();
			}
		}

		public override void getAndProcessHistoryData()
		{
			cumulus.LogDebugMessage("Lock: Station waiting for the lock");
			Cumulus.syncInit.Wait();
			cumulus.LogDebugMessage("Lock: Station has the lock");

			if (string.IsNullOrEmpty(cumulus.EcowittApplicationKey) || string.IsNullOrEmpty(cumulus.EcowittUserApiKey) || string.IsNullOrEmpty(cumulus.EcowittMacAddress))
			{
				cumulus.LogMessage("API.GetHistoricData: Missing Ecowitt API data in the configuration, aborting!");
				cumulus.LastUpdateTime = DateTime.Now;
			}
			else
			{
				int archiveRun = 0;

				try
				{

					api = new EcowittApi(cumulus, this);

					do
					{
						GetHistoricData();
						archiveRun++;
					} while (archiveRun < maxArchiveRuns);
				}
				catch (Exception ex)
				{
					cumulus.LogMessage("Exception occurred reading archive data: " + ex.Message);
				}
			}

			cumulus.LogDebugMessage("Lock: Station releasing the lock");
			_ = Cumulus.syncInit.Release();

			StartLoop();
		}

		private void GetHistoricData()
		{
			cumulus.LogMessage("GetHistoricData: Starting Historic Data Process");

			// add one minute to avoid duplicating the last log entry
			var startTime = cumulus.LastUpdateTime.AddMinutes(1);
			var endTime = DateTime.Now;

			// The API call is limited to fetching 24 hours of data
			if ((endTime - startTime).TotalHours > 24.0)
			{
				// only fetch 24 hours worth of data, and schedule another run to fetch the rest
				endTime = startTime.AddHours(24);
				maxArchiveRuns++;
			}

			api.GetHistoricData(startTime, endTime);

		}

		public string ProcessData(IHttpContext context, bool main, DateTime? ts = null)
		{
			/*
			 * Ecowitt doc:
			 *
			POST Parameters - all fields are URL escaped

			PASSKEY=<redacted>&stationtype=GW1000A_V1.6.8&dateutc=2021-07-23+17:13:34&tempinf=80.6&humidityin=50&baromrelin=29.940&baromabsin=29.081&tempf=81.3&humidity=43&winddir=296&windspeedmph=2.46&windgustmph=4.25&maxdailygust=14.09&solarradiation=226.28&uv=1&rainratein=0.000&eventrainin=0.000&hourlyrainin=0.000&dailyrainin=0.000&weeklyrainin=0.000&monthlyrainin=4.118&yearlyrainin=29.055&totalrainin=29.055&temp1f=83.48&humidity1=39&temp2f=87.98&humidity2=40&temp3f=82.04&humidity3=40&temp4f=93.56&humidity4=34&temp5f=-11.38&temp6f=87.26&humidity6=38&temp7f=45.50&humidity7=40&soilmoisture1=51&soilmoisture2=65&soilmoisture3=72&soilmoisture4=36&soilmoisture5=48&pm25_ch1=11.0&pm25_avg_24h_ch1=10.8&pm25_ch2=13.0&pm25_avg_24h_ch2=15.0&tf_co2=80.8&humi_co2=48&pm25_co2=4.8&pm25_24h_co2=6.1&pm10_co2=4.9&pm10_24h_co2=6.5&co2=493&co2_24h=454&lightning_time=1627039348&lightning_num=3&lightning=24&wh65batt=0&wh80batt=3.06&batt1=0&batt2=0&batt3=0&batt4=0&batt5=0&batt6=0&batt7=0&soilbatt1=1.5&soilbatt2=1.4&soilbatt3=1.5&soilbatt4=1.5&soilbatt5=1.6&pm25batt1=4&pm25batt2=4&wh57batt=4&co2_batt=6&freq=868M&model=GW1000_Pro
			PASSKEY=<redacted>&stationtype=GW1100A_V2.0.2&dateutc=2021-09-08+11:58:39&tempinf=80.8&humidityin=42&baromrelin=29.864&baromabsin=29.415&temp1f=87.8&tf_ch1=64.4&batt1=0&tf_batt1=1.48&freq=868M&model=GW1100A

			 */

			var procName = main ? "ProcessData" : "ProcessExtraData";

			if (starting || stopping)
			{
				context.Response.StatusCode = 200;
				return "success";
			}

			try
			{
				// PASSKEY
				// dateutc
				// freq
				// model

				cumulus.LogDebugMessage($"{procName}: Processing posted data");

				var text = new StreamReader(context.Request.InputStream).ReadToEnd();

				cumulus.LogDataMessage($"{procName}: Payload = {text}");

				// force the wind chill calculation as it is not present in historic data
				var chillSave = cumulus.StationOptions.CalculatedWC;
				cumulus.StationOptions.CalculatedWC = true;

				var retVal = ApplyData(text, main, ts);

				// restore wind chill setting
				cumulus.StationOptions.CalculatedWC = chillSave;

				if (retVal != "")
				{
					context.Response.StatusCode = 500;
					return retVal;
				}
			}
			catch (Exception ex)
			{
				cumulus.LogMessage($"{procName}: Error - {ex.Message}");
				context.Response.StatusCode = 500;
				return "Failed: General error - " + ex.Message;
			}

			cumulus.LogDebugMessage($"{procName}: Complete");

			context.Response.StatusCode = 200;
			return "success";
		}

		public string ApplyData(string dataString, bool main, DateTime? ts = null)
		{ 
			var procName = main ? "ApplyData" : "ApplyExtraData";
			var thisStation = main ? this : station;

			try
			{
				DateTime recDate;


				var data = HttpUtility.ParseQueryString(dataString);

				// We will ignore the dateutc field if this "live" data to avoid any clock issues
				recDate = ts.HasValue ? ts.Value : DateTime.Now;

				// we only really want to do this once
				if (reportStationType && !ts.HasValue)
				{
					cumulus.LogDebugMessage($"{procName}: StationType = {data["stationtype"]}, Model = {data["model"]}, Frequency = {data["freq"]}Hz");
					reportStationType = false;
				}

				// Only do the primary sensors if running as the main station
				if (main)
				{
					// === Wind ==
					try
					{
						// winddir
						// winddir_avg10m ??
						// windgustmph
						// windspeedmph
						// windspdmph_avg2m ??
						// windspdmph_avg10m ??
						// windgustmph_10m ??
						// maxdailygust

						var gust = data["windgustmph"];
						var dir = data["winddir"];
						var spd = data["windspeedmph"];


						if (gust == null || dir == null || spd == null)
						{
							cumulus.LogMessage($"ProcessData: Error, missing wind data");
						}
						else
						{
							var gustVal = ConvertWindMPHToUser(Convert.ToDouble(gust, invNum));
							var dirVal = Convert.ToInt32(dir, invNum);
							var spdVal = ConvertWindMPHToUser(Convert.ToDouble(spd, invNum));

							// The protocol does not provide an average value
							// so feed in current MX average
							DoWind(spdVal, dirVal, WindAverage / cumulus.Calib.WindSpeed.Mult, recDate);

							var gustLastCal = gustVal * cumulus.Calib.WindGust.Mult;
							if (gustLastCal > RecentMaxGust)
							{
								cumulus.LogDebugMessage("Setting max gust from current value: " + gustLastCal.ToString(cumulus.WindFormat));
								CheckHighGust(gustLastCal, dirVal, recDate);

								// add to recent values so normal calculation includes this value
								WindRecent[nextwind].Gust = gustVal; // use uncalibrated value
								WindRecent[nextwind].Speed = WindAverage / cumulus.Calib.WindSpeed.Mult;
								WindRecent[nextwind].Timestamp = recDate;
								nextwind = (nextwind + 1) % MaxWindRecent;

								RecentMaxGust = gustLastCal;
							}
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Wind data - " + ex.Message);
						return "Failed: Error in wind data - " + ex.Message;
					}


					// === Humidity ===
					try
					{
						// humidity
						// humidityin

						var humIn = data["humidityin"];
						var humOut = data["humidity"];


						if (humIn == null)
						{
							cumulus.LogMessage($"ProcessData: Error, missing indoor humidity");
						}
						else
						{
							var humVal = Convert.ToInt32(humIn, invNum);
							DoIndoorHumidity(humVal);
						}

						if (humOut == null)
						{
							cumulus.LogMessage("ProcessData: Error, missing outdoor humidity");
						}
						else
						{
							var humVal = Convert.ToInt32(humOut, invNum);
							DoOutdoorHumidity(humVal, recDate);
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Humidity data - " + ex.Message);
						return "Failed: Error in humidity data - " + ex.Message;
					}


					// === Pressure ===
					try
					{
						// baromabsin
						// baromrelin

						var press = data["baromrelin"];

						if (press == null)
						{
							cumulus.LogMessage($"ProcessData: Error, missing baro pressure");
						}
						else
						{
							var pressVal = ConvertPressINHGToUser(Convert.ToDouble(press, invNum));
							DoPressure(pressVal, recDate);
							UpdatePressureTrendString();
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Pressure data - " + ex.Message);
						return "Failed: Error in baro pressure data - " + ex.Message;
					}


					// === Indoor temp ===
					try
					{
						// tempinf

						var temp = data["tempinf"];

						if (temp == null)
						{
							cumulus.LogMessage($"ProcessData: Error, missing indoor temp");
						}
						else
						{
							var tempVal = ConvertTempFToUser(Convert.ToDouble(temp, invNum));
							DoIndoorTemp(tempVal);
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Indoor temp data - " + ex.Message);
						return "Failed: Error in indoor temp data - " + ex.Message;
					}


					// === Outdoor temp ===
					try
					{
						// tempf

						var temp = data["tempf"];

						if (temp == null)
						{
							cumulus.LogMessage($"ProcessData: Error, missing outdoor temp");
						}
						else
						{
							var tempVal = ConvertTempFToUser(Convert.ToDouble(temp, invNum));
							DoOutdoorTemp(tempVal, recDate);
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Outdoor temp data - " + ex.Message);
						return "Failed: Error in outdoor temp data - " + ex.Message;
					}


					// === Rain ===
					try
					{
						// rainin
						// hourlyrainin
						// dailyrainin
						// weeklyrainin
						// monthlyrainin
						// yearlyrainin
						// totalrainin - not reliable, depends on console and firmware version as to whether this is available or not.
						// rainratein
						// 24hourrainin Ambient only?
						// eventrainin

						var rain = data["yearlyrainin"];
						var rRate = data["rainratein"];

						if (rRate == null)
						{
							// No rain rate, so we will calculate it
							calculaterainrate = true;
							rRate = "0";
						}
						else
						{
							// we have a rain rate, so we will NOT calculate it
							calculaterainrate = false;
						}

						if (rain == null)
						{
							cumulus.LogMessage($"ProcessData: Error, missing rainfall");
						}
						else
						{
							var rainVal = ConvertRainINToUser(Convert.ToDouble(rain, invNum));
							var rateVal = ConvertRainINToUser(Convert.ToDouble(rRate, invNum));
							DoRain(rainVal, rateVal, recDate);
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Rain data - " + ex.Message);
						return "Failed: Error in rainfall data - " + ex.Message;
					}


					// === Dewpoint ===
					try
					{
						// dewptf

						var dewpnt = data["dewptf"];

						if (cumulus.StationOptions.CalculatedDP)
						{
							DoOutdoorDewpoint(0, recDate);
						}
						else if (dewpnt == null)
						{
							cumulus.LogMessage($"ProcessData: Error, missing dew point");
						}
						else
						{
							var val = ConvertTempFToUser(Convert.ToDouble(dewpnt, invNum));
							DoOutdoorDewpoint(val, recDate);
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Dew point data - " + ex.Message);
						return "Failed: Error in dew point data - " + ex.Message;
					}


					// === Wind Chill ===
					try
					{
						// windchillf

						if (cumulus.StationOptions.CalculatedWC && data["tempf"] != null && data["windspeedmph"] != null)
						{
							DoWindChill(0, recDate);
						}
						else
						{
							var chill = data["windchillf"];
							if (chill == null)
							{
								cumulus.LogMessage($"ProcessData: Error, missing dew point");
							}
							else
							{
								var val = ConvertTempFToUser(Convert.ToDouble(chill, invNum));
								DoWindChill(val, recDate);
							}
						}
					}
					catch (Exception ex)
					{
						cumulus.LogMessage("ProcessData: Error in Dew point data - " + ex.Message);
						return "Failed: Error in dew point data - " + ex.Message;
					}


					// === Humidex ===
					if (data["tempf"] != null && data["humidity"] != null)
					{
						DoHumidex(recDate);

						// === Apparent === - requires temp, hum, and windspeed
						if (data["windspeedmph"] != null)
						{
							DoApparentTemp(recDate);
							DoFeelsLike(recDate);
						}
						else
						{
							cumulus.LogMessage("ProcessData: Insufficient data to calculate Apparent/Feels Like temps");
						}
					}
					else
					{
						cumulus.LogMessage("ProcessData: Insufficient data to calculate Humidex and Apparent/Feels Like temps");
					}
				}

				// === Extra Temperature ===
				if (main || cumulus.EcowittExtraUseTempHum)
				{
					try
					{
						// temp[1-10]f
						ProcessExtraTemps(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in extra temperature data - {ex.Message}");
					}
				}

				// === Extra Humidity ===
				if (main || cumulus.EcowittExtraUseTempHum)
				{
					try
					{
						// humidity[1-10]
						ProcessExtraHumidity(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in extra humidity data - {ex.Message}");
					}
				}


				// === Solar ===
				if (main || cumulus.EcowittExtraUseSolar)
				{
					try
					{
						// solarradiation
						ProcessSolar(data, thisStation, recDate);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in solar data - {ex.Message}");
					}
				}


				// === UV ===
				if (main || cumulus.EcowittExtraUseUv)
				{
					try
					{
						// uv
						ProcessUv(data, thisStation, recDate);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in UV data - {ex.Message}");
					}
				}


				// === Soil Temp ===
				if (main || cumulus.EcowittExtraUseSoilTemp)
				{
					try
					{
						// soiltempf
						// soiltemp[2-16]f
						ProcessSoilTemps(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in Soil temp data - {ex.Message}");
					}
				}


				// === Soil Moisture ===
				if (main || cumulus.EcowittExtraUseSoilMoist)
				{
					try
					{
						// soilmoisture[1-16]
						ProcessSoilMoist(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in Soil moisture data - {ex.Message}");
					}
				}


				// === Leaf Wetness ===
				if (main || cumulus.EcowittExtraUseLeafWet)
				{
					try
					{
						// leafwetness
						// leafwetness[2-8]
						ProcessLeafWetness(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in Leaf wetness data - {ex.Message}");
					}
				}


				// === User Temp (Soil or Water) ===
				if (main || cumulus.EcowittExtraUseUserTemp)
				{
					try
					{
						// tf_ch[1-8]
						ProcessUserTemp(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in User Temp data - {ex.Message}");
					}
				}


				// === Air Quality ===
				if (main || cumulus.EcowittExtraUseAQI)
				{
					try
					{
						// pm25_ch[1-4]
						// pm25_avg_24h_ch[1-4]
						ProcessAirQuality(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in Air Quality data - {ex.Message}");
					}
				}


				// === CO₂ ===
				if (main || cumulus.EcowittExtraUseCo2)
				{
					try
					{
						// tf_co2
						// humi_co2
						// pm25_co2
						// pm25_24h_co2
						// pm10_co2
						// pm10_24h_co2
						// co2
						// co2_24h
						ProcessCo2(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in CO₂ data - {ex.Message}");
					}
				}


				// === Lightning ===
				if (main || cumulus.EcowittExtraUseLightning)
				{
					try
					{
						// lightning
						// lightning_time
						// lightning_num
						ProcessLightning(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in Lightning data - {ex.Message}");
					}
				}


				// === Leak ===
				if (main || cumulus.EcowittExtraUseLeak)
				{
					try
					{
						// leak[1 - 4]
						ProcessLeak(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error in Leak data - {ex.Message}");
					}
				}


				// === Batteries ===
				try
				{
					/*
					wh25batt
					wh26batt
					wh32batt
					wh40batt
					wh57batt
					wh65batt
					wh68batt
					wh80batt
					wh90batt
					batt[1-8] (wh31)
					soilbatt[1-8] (wh51)
					pm25batt[1-4] (wh41/wh43)
					leakbatt[1-4] (wh55)
					co2_batt
					*/

					ProcessBatteries(data);
				}
				catch (Exception ex)
				{
					cumulus.LogMessage($"{procName}: Error in Battery data - {ex.Message}");
				}


				// === Extra Dew point ===
				if (main || cumulus.EcowittExtraUseTempHum)
				{
					try
					{
						ProcessExtraDewPoint(data, thisStation);
					}
					catch (Exception ex)
					{
						cumulus.LogMessage($"{procName}: Error calculating extra sensor dew points - {ex.Message}");
					}
				}

				// === Firmware Version ===
				try
				{
					if (data["stationtype"] != null)
					{
						var fwString = data["stationtype"].Split(new string[] { "_V" }, StringSplitOptions.None);
						if (fwString.Length > 1)
						{
							// bug fix for WS90 which sends "stationtype=GW2000A_V2.1.0, runtime=253500"
							var str = fwString[1].Split(new string[] { ", " }, StringSplitOptions.None)[0];
							GW1000FirmwareVersion = str;
						}
					}
				}
				catch (Exception ex)
				{
					cumulus.LogMessage($"{procName}: Error extracting firmware version - {ex.Message}");
				}


				DoForecast(string.Empty, false);

				UpdateStatusPanel(recDate);
				UpdateMQTT();
			}
			catch (Exception ex)
			{
				cumulus.LogMessage($"{procName}: Error - {ex.Message}");
				return "Failed: General error - " + ex.Message;
			}

			return "";
		}

		private void ProcessExtraTemps(NameValueCollection data, WeatherStation station)
		{
			for (var i = 1; i <= 10; i++)
			{
				if (data["temp" + i + "f"] != null)
				{
					station.DoExtraTemp(ConvertTempFToUser(Convert.ToDouble(data["temp" + i + "f"], invNum)), i);
				}
			}
		}

		private void ProcessExtraHumidity(NameValueCollection data, WeatherStation station)
		{
			for (var i = 1; i <= 10; i++)
			{
				if (data["humidity" + i] != null)
				{
					station.DoExtraHum(Convert.ToDouble(data["humidity" + i], invNum), i);
				}
			}
		}

		private void ProcessSolar(NameValueCollection data, WeatherStation station, DateTime recDate)
		{
			if (data["solarradiation"] != null)
			{
				station.DoSolarRad((int)Convert.ToDouble(data["solarradiation"], invNum), recDate);
			}
		}

		private void ProcessUv(NameValueCollection data, WeatherStation station, DateTime recDate)
		{
			if (data["uv"] != null)
			{
				station.DoUV(Convert.ToDouble(data["uv"], invNum), recDate);
			}
		}

		private void ProcessSoilTemps(NameValueCollection data, WeatherStation station)
		{
			if (data["soiltempf"] != null)
			{
				station.DoSoilTemp(ConvertTempFToUser(Convert.ToDouble(data["soiltempf"], invNum)), 1);
			}

			for (var i = 2; i <= 16; i++)
			{
				if (data["soiltemp" + i + "f"] != null)
				{
					station.DoSoilTemp(ConvertTempFToUser(Convert.ToDouble(data["soiltemp" + i + "f"], invNum)), i - 1);
				}
			}
		}

		private void ProcessSoilMoist(NameValueCollection data, WeatherStation station)
		{
			for (var i = 1; i <= 16; i++)
			{
				if (data["soilmoisture" + i] != null)
				{
					station.DoSoilMoisture(Convert.ToDouble(data["soilmoisture" + i], invNum), i);
				}
			}
		}

		private void ProcessLeafWetness(NameValueCollection data, WeatherStation station)
		{
			if (data["leafwetness"] != null)
			{
				station.DoLeafWetness(Convert.ToInt32(data["leafwetness"], invNum), 1);
			}
			// Though Ecowitt supports up to 8 sensors, MX only supports the first 4
			for (var i = 1; i <= 8; i++)
			{
				if (data["leafwetness_ch" + i] != null)
				{
					station.DoLeafWetness(Convert.ToInt32(data["leafwetness_ch" + i], invNum), i);
				}
			}

		}

		private void ProcessUserTemp(NameValueCollection data, WeatherStation station)
		{
			for (var i = 1; i <= 8; i++)
			{
				if (data["tf_ch" + i] != null)
				{
					station.DoUserTemp(ConvertTempFToUser(Convert.ToDouble(data["tf_ch" + i], invNum)), i);
				}
			}
		}

		private void ProcessAirQuality(NameValueCollection data, WeatherStation station)
		{
			// pm25_ch[1-4]
			// pm25_avg_24h_ch[1-4]

			for (var i = 1; i <= 4; i++)
			{
				var pm = data["pm25_ch" + i];
				var pmAvg = data["pm25_avg_24h_ch" + i];
				if (pm != null)
				{
					station.DoAirQuality(Convert.ToDouble(pm, invNum), i);
				}
				if (pmAvg != null)
				{
					station.DoAirQualityAvg(Convert.ToDouble(pmAvg, invNum), i);
				}
			}
		}

		private void ProcessCo2(NameValueCollection data, WeatherStation station)
		{
			// tf_co2
			// humi_co2
			// pm25_co2
			// pm25_24h_co2
			// pm10_co2
			// pm10_24h_co2
			// co2
			// co2_24h

			if (data["tf_co2"] != null)
			{
				station.CO2_temperature = ConvertTempFToUser(Convert.ToDouble(data["tf_co2"], invNum));
			}
			if (data["humi_co2"] != null)
			{
				station.CO2_humidity = Convert.ToInt32(data["humi_co2"], invNum);
			}
			if (data["pm25_co2"] != null)
			{
				station.CO2_pm2p5 = Convert.ToDouble(data["pm25_co2"], invNum);
			}
			if (data["pm25_24h_co2"] != null)
			{
				station.CO2_pm2p5_24h = Convert.ToDouble(data["pm25_24h_co2"], invNum);
			}
			if (data["pm10_co2"] != null)
			{
				station.CO2_pm10 = Convert.ToDouble(data["pm10_co2"], invNum);
			}
			if (data["pm10_24h_co2"] != null)
			{
				station.CO2_pm10_24h = Convert.ToDouble(data["pm10_24h_co2"], invNum);
			}
			if (data["co2"] != null)
			{
				station.CO2 = Convert.ToInt32(data["co2"], invNum);
			}
			if (data["co2_24h"] != null)
			{
				station.CO2_24h = Convert.ToInt32(data["co2_24h"], invNum);
			}
		}

		private void ProcessLightning(NameValueCollection data, WeatherStation station)
		{
			var dist = data["lightning"];
			var time = data["lightning_time"];
			var num = data["lightning_num"];

			if (!string.IsNullOrEmpty(dist) && !string.IsNullOrEmpty(time))
			{
				// Only set the lightning time/distance if it is newer than what we already have - the GW1000 seems to reset this value
				var valDist = Convert.ToDouble(dist, invNum);
				if (valDist != 255)
				{
					station.LightningDistance = ConvertKmtoUserUnits(valDist);
				}

				var valTime = Convert.ToDouble(time, invNum);
				// Sends a default value until the first strike is detected of 0xFFFFFFFF
				if (valTime != 0xFFFFFFFF)
				{
					var dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
					dtDateTime = dtDateTime.AddSeconds(valTime).ToLocalTime();

					if (dtDateTime > LightningTime)
					{
						station.LightningTime = dtDateTime;
					}
				}
			}

			if (!string.IsNullOrEmpty(num))
			{
				station.LightningStrikesToday = Convert.ToInt32(num, invNum);
			}
		}

		private void ProcessLeak(NameValueCollection data, WeatherStation station)
		{
			for (var i = 1; i <= 4; i++)
			{
				if (data["leak" + i] != null)
				{
					station.DoLeakSensor(Convert.ToInt32(data["leak" + i], invNum), i);
				}
			}
		}

		private void ProcessBatteries(NameValueCollection data)
		{
			var lowBatt = false;
			lowBatt = lowBatt || (data["wh25batt"] != null && data["wh25batt"] == "1");
			lowBatt = lowBatt || (data["wh26batt"] != null && data["wh26batt"] == "1");
			lowBatt = lowBatt || (data["wh40batt"] != null && data["wh40batt"] == "1");
			lowBatt = lowBatt || (data["wh40batt"] != null && data["wh40batt"] == "1");
			lowBatt = lowBatt || (data["wh57batt"] != null && data["wh57batt"] == "1");
			lowBatt = lowBatt || (data["wh65batt"] != null && data["wh65batt"] == "1");
			lowBatt = lowBatt || (data["wh68batt"] != null && Convert.ToDouble(data["wh68batt"], invNum) <= 1.2);
			lowBatt = lowBatt || (data["wh80batt"] != null && Convert.ToDouble(data["wh80batt"], invNum) <= 1.2);
			lowBatt = lowBatt || (data["wh90batt"] != null && Convert.ToDouble(data["wh90batt"], invNum) <= 2.4);
			for (var i = 1; i < 5; i++)
			{
				lowBatt = lowBatt || (data["batt" + i]     != null && data["batt" + i] == "1");
				lowBatt = lowBatt || (data["soilbatt" + i] != null && Convert.ToDouble(data["soilbatt" + i], invNum) <= 1.2);
				lowBatt = lowBatt || (data["pm25batt" + i] != null && data["pm25batt" + i] == "1");
				lowBatt = lowBatt || (data["leakbatt" + i] != null && data["leakbatt" + i] == "1");
				lowBatt = lowBatt || (data["tf_batt" + i]  != null && Convert.ToDouble(data["tf_batt" + i], invNum) <= 1.2);
				lowBatt = lowBatt || (data["leaf_batt" + i] != null && Convert.ToDouble(data["leaf_batt" + i], invNum) <= 1.2);
			}
			for (var i = 5; i < 9; i++)
			{
				lowBatt = lowBatt || (data["batt" + i]     != null && data["batt" + i] == "1");
				lowBatt = lowBatt || (data["soilbatt" + i] != null && Convert.ToDouble(data["soilbatt" + i], invNum) <= 1.2);
				lowBatt = lowBatt || (data["tf_batt" + i]  != null && Convert.ToDouble(data["tf_batt" + i], invNum) <= 1.2);
				lowBatt = lowBatt || (data["leaf_batt" + i] != null && Convert.ToDouble(data["leaf_batt" + i], invNum) <= 1.2);
			}

			cumulus.BatteryLowAlarm.Triggered = lowBatt;
		}

		private void ProcessExtraDewPoint(NameValueCollection data, WeatherStation station)
		{
			for (var i = 1; i <= 10; i++)
			{
				if (data["temp" + i + "f"] != null && data["humidity" + i] != null)
				{
					var dp = MeteoLib.DewPoint(ConvertUserTempToC(station.ExtraTemp[i]), station.ExtraHum[i]);
					station.ExtraDewPoint[i] = ConvertTempCToUser(dp);
				}
			}
		}

	}
}