using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Browser;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Vdms.Hls.Mss.HLSMSSImplementation;

namespace SilverlightMediaElement
{
	[ScriptableType]
	public partial class MainPage : UserControl, IVariantSelector
	{
		private readonly DispatcherTimer _timer;
		private readonly TimeSpan _bufferLength = TimeSpan.FromSeconds(30.0);

		// work arounds for src, load(), play() compatibility
		private bool _isLoading;
		private bool _isAttemptingToPlay;

		// variables
		private string _mediaUrl;
		private readonly string _preload;
		private readonly string _htmlid;
		private readonly bool _autoplay;
		private readonly bool _debug;
		private readonly int _width;
		private readonly int _height;
		private readonly int _timerRate;
		private double _bufferedBytes;
		private double _bufferedTime;
		private readonly double _volume = 1;
		private int _videoWidth;
		private int _videoHeight;

		// state
		private bool _isPaused = true;
		private bool _isEnded;

		// dummy
		private bool _firedCanPlay;

		// mediaElement.Position updates TimelineSlider.Value, and
		// updating TimelineSlider.Value updates mediaElement.Position, 
		// this variable helps us break the infinite loop
		private bool duringTickEvent;

		private bool playVideoWhenSliderDragIsOver;

		protected HLSMediaStreamSource _mss;
		private List<HLSVariant> _sortedAvailableVariants;
		private volatile BitrateCommand _bitrateCommand;

		public MainPage(IDictionary<string, string> initParams)
		{
			InitializeComponent();

			HtmlPage.RegisterScriptableObject("MediaElementJS", this);

			// add events
			media.BufferingProgressChanged += media_BufferingProgressChanged;
			media.DownloadProgressChanged += media_DownloadProgressChanged;
			media.CurrentStateChanged += media_CurrentStateChanged;
			media.MediaEnded += media_MediaEnded;
			media.MediaFailed += media_MediaFailed;
			media.MediaOpened += media_MediaOpened;
			media.MouseLeftButtonDown += media_MouseLeftButtonDown;
			CompositionTarget.Rendering += CompositionTarget_Rendering;
			transportControls.Visibility = Visibility.Collapsed;

			// get parameters
			if (initParams.ContainsKey("id"))
			{
				_htmlid = initParams["id"];
			}
			if (initParams.ContainsKey("file"))
			{
				_mediaUrl = initParams["file"];
			}
			if (initParams.ContainsKey("autoplay") && initParams["autoplay"] == "true")
			{
				_autoplay = true;
			}
			if (initParams.ContainsKey("debug") && initParams["debug"] == "true")
			{
				_debug = true;
			}
			if (initParams.ContainsKey("preload"))
			{
				_preload = initParams["preload"].ToLower();
			}
			else
			{
				_preload = "";
			}

			if (!(new[] { "none", "metadata", "auto" }).Contains(_preload))
			{
				_preload = "none";
			}

			if (initParams.ContainsKey("width"))
			{
				Int32.TryParse(initParams["width"], out _width);
			}
			if (initParams.ContainsKey("height"))
			{
				Int32.TryParse(initParams["height"], out _height);
			}
			if (initParams.ContainsKey("timerate"))
			{
				Int32.TryParse(initParams["timerrate"], out _timerRate);
			}
			if (initParams.ContainsKey("startvolume"))
			{
				Double.TryParse(initParams["startvolume"], out _volume);
			}

			if (_timerRate == 0)
			{
				_timerRate = 250;
			}

			// timer
			_timer = new DispatcherTimer();
			_timer.Interval = new TimeSpan(0, 0, 0, 0, _timerRate); // 200 Milliseconds 
			_timer.Tick += timer_Tick;
			_timer.Stop();

			//_mediaUrl = "http://local.mediaelement.com/media/jsaddington.mp4";
			//_autoplay = true;

			// set stage and media sizes
			if (_width > 0)
			{
				LayoutRoot.Width = media.Width = this.Width = _width;
			}
			if (_height > 0)
			{
				LayoutRoot.Height = media.Height = this.Height = _height;
			}

			// debug
			debugPanel.Visibility = (_debug) ? Visibility.Visible : Visibility.Collapsed;
			txtId.Text = "ID: " + _htmlid;
			txtFile.Text = "File: " + _mediaUrl;

			media.AutoPlay = _autoplay;
			media.Volume = _volume;
			if (!String.IsNullOrEmpty(_mediaUrl))
			{
				setSrc(_mediaUrl);
				if (_autoplay || _preload != "none")
				{
					loadMedia();
				}
			}

			media.MouseLeftButtonUp += media_MouseLeftButtonUp;

			// full screen settings
			Application.Current.Host.Content.FullScreenChanged += DisplaySizeInformation;
			Application.Current.Host.Content.Resized += DisplaySizeInformation;
			//FullscreenButton.Visibility = System.Windows.Visibility.Collapsed;

			// send out init call			
			//HtmlPage.Window.Invoke("html5_MediaPluginBridge_initPlugin", new object[] {_htmlid});
			try
			{
				HtmlPage.Window.Eval("mejs.MediaPluginBridge.initPlugin('" + _htmlid + "');");
			}
			catch
			{
			}
		}

		private void media_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			switch (media.CurrentState)
			{
				case MediaElementState.Playing:
					pauseMedia();
					break;

				case MediaElementState.Paused:
					playMedia();
					break;
				case MediaElementState.Stopped:

					break;
				case MediaElementState.Buffering:
					pauseMedia();
					break;
			}
		}

		private void media_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			SendEvent("click");
		}

		private void media_MediaOpened(object sender, RoutedEventArgs e)
		{
			_videoWidth = Convert.ToInt32(media.NaturalVideoWidth);
			_videoHeight = Convert.ToInt32(media.NaturalVideoHeight);

			var duration = media.NaturalDuration.TimeSpan;
			totalTimeTextBlock.Text = TimeSpanToString(duration);
			UpdateVideoSize();

			playPauseButton.IsChecked = true;

			SendEvent("loadedmetadata");
		}

		private void timer_Tick(object sender, EventArgs e)
		{
			SendEvent("timeupdate");
		}

		private void StartTimer()
		{
			_timer.Start();
		}

		private void StopTimer()
		{
			_timer.Stop();
		}

		private void media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
		{
			SendEvent(e.ToString());
		}

		private void media_MediaEnded(object sender, RoutedEventArgs e)
		{
			_isEnded = true;
			_isPaused = false;
			SendEvent("ended");
			StopTimer();
		}

		private void media_CurrentStateChanged(object sender, RoutedEventArgs e)
		{
			txtState.Text = "State: " + media.CurrentState;

			switch (media.CurrentState)
			{
				case MediaElementState.Opening:
					SendEvent("loadstart");
					break;
				case MediaElementState.Playing:
					_isEnded = false;
					_isPaused = false;
					_isAttemptingToPlay = false;
					StartTimer();

					SendEvent("play");
					SendEvent("playing");
					break;

				case MediaElementState.Paused:
					_isEnded = false;
					_isPaused = true;

					// special settings to allow play() to work
					_isLoading = false;
					txtLastMessage.Text = "Message: paused event, playing = " + _isAttemptingToPlay;
					if (_isAttemptingToPlay)
					{
						this.playMedia();
					}

					StopTimer();
					SendEvent("paused");
					break;
				case MediaElementState.Stopped:
					_isEnded = false;
					_isPaused = true;
					StopTimer();
					SendEvent("paused");
					break;
				case MediaElementState.Buffering:
					SendEvent("progress");
					break;
			}
		}

		private void media_BufferingProgressChanged(object sender, RoutedEventArgs e)
		{
			_bufferedTime = media.DownloadProgress * media.NaturalDuration.TimeSpan.TotalSeconds;
			_bufferedBytes = media.BufferingProgress;

			SendEvent("progress");
		}

		private void media_DownloadProgressChanged(object sender, RoutedEventArgs e)
		{
			_bufferedTime = media.DownloadProgress * media.NaturalDuration.TimeSpan.TotalSeconds;
			_bufferedBytes = media.BufferingProgress;

			if (!_firedCanPlay)
			{
				SendEvent("loadeddata");
				SendEvent("canplay");
				_firedCanPlay = true;
			}

			SendEvent("progress");
		}

		private void SendEvent(string name)
		{
			/*
			 * INVOKE
			HtmlPage.Window.Invoke("html5_MediaPluginBridge_fireEvent", 
					_htmlid,
					name,					
					@"'{" + 
						@"""name"": """ + name + @"""" +
						@", ""currentTime"":" + (media.Position.TotalSeconds).ToString() + @"" +
						@", ""duration"":" + (media.NaturalDuration.TimeSpan.TotalSeconds).ToString() + @"" +
						@", ""paused"":" + (_isEnded).ToString().ToLower() + @"" +
						@", ""muted"":" + (media.IsMuted).ToString().ToLower() + @"" +
						@", ""ended"":" + (_isPaused).ToString().ToLower() + @"" +
						@", ""volume"":" + (media.Volume).ToString() + @"" +
						@", ""bufferedBytes"":" + (_bufferedBytes).ToString() + @"" +
						@", ""bufferedTime"":" + (_bufferedTime).ToString() + @"" +
					@"}'");
			 */

			/*
			 * EVAL
			HtmlPage.Window.Eval("mejs.MediaPluginBridge.fireEvent('" + _htmlid + "','" + name + "'," +
					@"{" +
						@"""name"": """ + name + @"""" +
						@", ""currentTime"":" + (media.Position.TotalSeconds).ToString() + @"" +
						@", ""duration"":" + (media.NaturalDuration.TimeSpan.TotalSeconds).ToString() + @"" +
						@", ""paused"":" + (_isEnded).ToString().ToLower() + @"" +
						@", ""muted"":" + (media.IsMuted).ToString().ToLower() + @"" +
						@", ""ended"":" + (_isPaused).ToString().ToLower() + @"" +
						@", ""volume"":" + (media.Volume).ToString() + @"" +
						@", ""bufferedBytes"":" + (_bufferedBytes).ToString() + @"" +
						@", ""bufferedTime"":" + (_bufferedTime).ToString() + @"" +
					@"});");
			 * */

			// setTimeout
			try
			{
				var invCulture = CultureInfo.InvariantCulture;

				HtmlPage.Window.Invoke("setTimeout", "mejs.MediaPluginBridge.fireEvent('" + _htmlid + "','" + name + "'," +
				                                     @"{" +
				                                     @"""name"": """ + name + @"""" +
				                                     @", ""currentTime"":" + (media.Position.TotalSeconds).ToString(invCulture)
				                                     + @"" +
				                                     @", ""duration"":"
				                                     + (media.NaturalDuration.TimeSpan.TotalSeconds).ToString(invCulture) + @"" +
				                                     @", ""paused"":" + (_isPaused).ToString().ToLower() + @"" +
				                                     @", ""muted"":" + (media.IsMuted).ToString().ToLower() + @"" +
				                                     @", ""ended"":" + (_isEnded).ToString().ToLower() + @"" +
				                                     @", ""volume"":" + (media.Volume).ToString(invCulture) + @"" +
				                                     @", ""bufferedBytes"":" + (_bufferedBytes).ToString(invCulture) + @"" +
				                                     @", ""bufferedTime"":" + (_bufferedTime).ToString(invCulture) + @"" +
				                                     @", ""videoWidth"":" + (_videoWidth) + @"" +
				                                     @", ""videoHeight"":" + (_videoHeight) + @"" +
				                                     @"});", 0);
			}
			catch
			{
			}
		}

		/* HTML5 wrapper methods */

		[ScriptableMember]
		public void playMedia()
		{
			txtLastMessage.Text = "Message: method:play " + media.CurrentState;

			// sometimes people forget to call load() first
			if (_mediaUrl != "" && media.Source == null)
			{
				var uri = new Uri(_mediaUrl, UriKind.Absolute);
				var path = String.Format("{0}{1}{2}", uri.Scheme, Uri.SchemeDelimiter, uri.AbsolutePath);
				var ext = Path.GetExtension(path);
				if (ext.ToLowerInvariant() != ".m3u8" || (ext.ToLowerInvariant() == ".m3u8" && _mss == null))
				{
					_isAttemptingToPlay = true;
					loadMedia();
				}
			}

			// store and trigger with the state change above
			if (media.CurrentState == MediaElementState.Closed && _isLoading)
			{
				txtLastMessage.Text = "Message: storing _isAttemptingToPlay ";
				_isAttemptingToPlay = true;
			}

			media.Play();
			_isEnded = false;
			_isPaused = false;

			playPauseButton.IsChecked = true;

			//StartTimer();
		}

		[ScriptableMember]
		public void pauseMedia()
		{
			txtLastMessage.Text = "Message: method:pause " + media.CurrentState;

			_isEnded = false;
			_isPaused = true;

			media.Pause();
			StopTimer();
			playPauseButton.IsChecked = false;
		}

		[ScriptableMember]
		public void loadMedia()
		{
			_isLoading = true;
			_firedCanPlay = false;

			txtLastMessage.Text = "Message: method:load " + media.CurrentState + " " + _mediaUrl;

			var uri = new Uri(_mediaUrl, UriKind.Absolute);
			var path = String.Format("{0}{1}{2}", uri.Scheme, Uri.SchemeDelimiter, uri.AbsolutePath);
			var ext = Path.GetExtension(path);
			media.Source = new Uri(_mediaUrl, UriKind.Absolute);
			if (ext.ToLowerInvariant() == ".m3u8")
			{
				var openParam = new HLSMediaStreamSourceOpenParam();
				openParam.uri = uri;
				if (_mss != null)
				{
					_mss.Dispose();
				}
				_mss = new HLSMediaStreamSource(openParam);
				_mss.BufferLength = this._bufferLength;
				_mss.Playback.DownloadBitrateChanged += Async_DownloadBitrateChanged;
				_mss.Playback.PlaybackBitrateChanged += Async_PlaybackBitrateChanged;
				_mss.Playback.MediaFileChanged += Async_MediaFileChanged;
				_mss.Playback.VariantSelector = this;
				media.SetSource(this._mss);
				this._bitrateCommand = BitrateCommand.Auto;

				DispatcherTimer dispatcherTimer = new DispatcherTimer();
				dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 500);
				dispatcherTimer.Tick += new EventHandler(this.Timer_Tick);
				dispatcherTimer.Start();

			}
			//media.Play();
			//media.Stop();
		}

		[ScriptableMember]
		public void stopMedia()
		{
			txtLastMessage.Text = "Message: method:stop " + media.CurrentState;

			_isEnded = true;
			_isPaused = false;

			media.Stop();
			StopTimer();
			playPauseButton.IsChecked = false;
		}

		[ScriptableMember]
		public void setVolume(Double volume)
		{
			txtLastMessage.Text = "Message: method:setvolume: " + volume;

			media.Volume = volume;

			SendEvent("volumechange");
		}

		[ScriptableMember]
		public void setMuted(bool isMuted)
		{
			txtLastMessage.Text = "Message: method:setmuted: " + isMuted;

			media.IsMuted = isMuted;
			muteButton.IsChecked = isMuted;
			SendEvent("volumechange");
		}

		[ScriptableMember]
		public void setCurrentTime(Double position)
		{
			txtLastMessage.Text = "Message: method:setCurrentTime: " + position;

			var milliseconds = Convert.ToInt32(position * 1000);

			SendEvent("seeking");
			media.Position = new TimeSpan(0, 0, 0, 0, milliseconds);
			SendEvent("seeked");
		}

		[ScriptableMember]
		public void setSrc(string url)
		{
			_mediaUrl = url;
		}

		[ScriptableMember]
		public void setFullscreen(bool goFullscreen)
		{
			FullscreenButton.Visibility = Visibility.Visible;
		}

		[ScriptableMember]
		public void setVideoSize(int width, int height)
		{
			this.Width = media.Width = width;
			this.Height = media.Height = height;
		}

		[ScriptableMember]
		public void positionFullscreenButton(int x, int y, bool visibleAndAbove)
		{
			if (visibleAndAbove)
			{
				//FullscreenButton.Visibility = System.Windows.Visibility.Collapsed;
			}
			else
			{
				//FullscreenButton.Visibility = System.Windows.Visibility.Visible;
			}
		}

		private void FullscreenButton_Click(object sender, RoutedEventArgs e)
		{
			Application.Current.Host.Content.IsFullScreen = true;
			//FullscreenButton.Visibility = System.Windows.Visibility.Collapsed;
		}

		private void DisplaySizeInformation(Object sender, EventArgs e)
		{
			this.Width = LayoutRoot.Width = media.Width = Application.Current.Host.Content.ActualWidth;
			this.Height = LayoutRoot.Height = media.Height = Application.Current.Host.Content.ActualHeight;

			UpdateVideoSize();
		}

		#region play button

		private void BigPlayButton_Click(object sender, RoutedEventArgs e)
		{
			playPauseButton.IsChecked = true;
			PlayPauseButton_Click(sender, e);
		}

		private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
		{
			bigPlayButton.Visibility = Visibility.Collapsed;

			// this will be the toggle button state after the click has been processed
			if (playPauseButton.IsChecked == true)
			{
				playMedia();
			}
			else
			{
				pauseMedia();
			}
		}

		#endregion

		#region timelineSlider

		private void Seek(double percentComplete)
		{
			if (duringTickEvent)
			{
				throw new Exception("Can't call Seek() now, you'll get an infinite loop");
			}

			var duration = media.NaturalDuration.TimeSpan;
			var newPosition = (int)(duration.TotalSeconds * percentComplete);
			media.Position = new TimeSpan(0, 0, newPosition);

			// let the next CompositionTarget.Rendering take care of updating the text blocks
		}

		private Slider GetSliderParent(object sender)
		{
			var element = (FrameworkElement)sender;
			do
			{
				element = (FrameworkElement)VisualTreeHelper.GetParent(element);
			}
			while (!(element is Slider));
			return (Slider)element;
		}

		private void LeftTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			e.Handled = true;
			var lefttrack = (sender as FrameworkElement).FindName("LeftTrack") as FrameworkElement;
			var righttrack = (sender as FrameworkElement).FindName("RightTrack") as FrameworkElement;
			var position = e.GetPosition(lefttrack).X;
			var width =
				righttrack.TransformToVisual(lefttrack).Transform(new Point(righttrack.ActualWidth, righttrack.ActualHeight)).X;
			var percent = position / width;
			var slider = GetSliderParent(sender);
			slider.Value = percent;
		}

		private void HorizontalThumb_DragStarted(object sender, DragStartedEventArgs e)
		{
			if (GetSliderParent(sender) != timelineSlider)
			{
				return;
			}

			var notPlaying = (media.CurrentState == MediaElementState.Paused
			                  || media.CurrentState == MediaElementState.Stopped);

			if (notPlaying)
			{
				playVideoWhenSliderDragIsOver = false;
			}
			else
			{
				playVideoWhenSliderDragIsOver = true;
				media.Pause();
			}
		}

		private void HorizontalThumb_DragCompleted(object sender, DragCompletedEventArgs e)
		{
			if (playVideoWhenSliderDragIsOver)
			{
				media.Play();
			}
		}

		private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			if (duringTickEvent)
			{
				return;
			}

			Seek(timelineSlider.Value);
		}

		#endregion

		#region updating current time

		private void CompositionTarget_Rendering(object sender, EventArgs e)
		{
			duringTickEvent = true;

			var duration = media.NaturalDuration.TimeSpan;
			if (duration.TotalSeconds != 0)
			{
				var percentComplete = (media.Position.TotalSeconds / duration.TotalSeconds);
				timelineSlider.Value = percentComplete;
				var text = TimeSpanToString(media.Position);
				if (this.currentTimeTextBlock.Text != text)
				{
					this.currentTimeTextBlock.Text = text;
				}
			}

			duringTickEvent = false;
		}

		private static string TimeSpanToString(TimeSpan time)
		{
			return string.Format("{0:00}:{1:00}", (time.Hours * 60) + time.Minutes, time.Seconds);
		}

		#endregion

		private void MuteButton_Click(object sender, RoutedEventArgs e)
		{
			//media.IsMuted = (bool)muteButton.IsChecked;
			setMuted((bool)muteButton.IsChecked);
		}

		#region fullscreen mode

		private void FullScreenButton_Click(object sender, RoutedEventArgs e)
		{
			var content = Application.Current.Host.Content;
			content.IsFullScreen = !content.IsFullScreen;
		}

		private void Content_FullScreenChanged(object sender, EventArgs e)
		{
			UpdateVideoSize();
		}

		private void UpdateVideoSize()
		{
			if (Application.Current.Host.Content.IsFullScreen)
			{
				transportControls.Visibility = Visibility.Visible;
				// mediaElement takes all available space
				//VideoRow.Height = new GridLength(1, GridUnitType.Star);
				//VideoColumn.Width = new GridLength(1, GridUnitType.Star);
			}
			else
			{
				transportControls.Visibility = Visibility.Collapsed;
				// mediaElement is only as big as the source video
				//VideoRow.Height = new GridLength(1, GridUnitType.Auto);
				//VideoColumn.Width = new GridLength(1, GridUnitType.Auto);
			}
		}

		#endregion

		private enum BitrateCommand
		{
			IncreaseBitrate,
			DecreaseBitrate,
			Random,
			DoNotChange,
			Auto,
		}

		private class HLSVariantBitrateComparer : IComparer<HLSVariant>
		{
			public int Compare(HLSVariant x, HLSVariant y)
			{
				if (x == null)
				{
					return 1;
				}
				if (y == null)
				{
					return -1;
				}
				else
				{
					return (int)x.Bitrate == (int)y.Bitrate ? 0 : (x.Bitrate < y.Bitrate ? -1 : 1);
				}
			}
		}

		private void Async_MediaFileChanged(object sender, Uri uri)
		{
			this.Dispatcher.BeginInvoke(new Action<object, Uri>(this.Playback_MediaFileChanged), new object[2]
			                                                                                     	{
			                                                                                     		sender,
			                                                                                     		uri
			                                                                                     	});
		}

		private void Playback_MediaFileChanged(object sender, Uri uri)
		{
			txtFile.Text = "File: " + uri.ToString();
		}

		private void Async_DownloadBitrateChanged(object sender, uint newBandwidth)
		{
			this.Dispatcher.BeginInvoke(new Action<uint>(this.MSS_OnDownloadBitrateChanged), new object[1]
			                                                                                 	{
			                                                                                 		newBandwidth
			                                                                                 	});
		}

		private void Async_PlaybackBitrateChanged(object sender, uint newBandwidth)
		{
			this.Dispatcher.BeginInvoke(new Action<uint>(this.MSS_OnPlaybackBitrateChanged), new object[1]
			                                                                                 	{
			                                                                                 		newBandwidth
			                                                                                 	});
		}

		private void MSS_OnPlaybackBitrateChanged(uint newBandwidth)
		{
			txtPlaybackQuality.Text = "PB Quality: " + newBandwidth + " bps";
		}

		private void MSS_OnDownloadBitrateChanged(uint newBandwidth)
		{
			txtDownloadQuality.Text = "DL Quality: " + newBandwidth + " bps";
		}

		private void Timer_Tick(object sender, EventArgs e)
		{
			string[] strArray1 = new string[10];
			strArray1[0] = "Buffer Level: ";
			string[] strArray2 = strArray1;
			int index = 1;
			TimeSpan timeSpan = this._mss.BufferLevel;
			double num = timeSpan.TotalMilliseconds * 100.0;
			timeSpan = this._bufferLength;
			double totalMilliseconds = timeSpan.TotalMilliseconds;
			string str1 = ((int)(num / totalMilliseconds)).ToString();
			strArray2[index] = str1;
			strArray1[2] = "% ";
			strArray1[3] = ((int)this._mss.BufferLevel.TotalMilliseconds).ToString();
			strArray1[4] = "ms ";
			strArray1[5] = (this._mss.BufferLevelInBytes / 1024L).ToString();
			strArray1[6] = "KB  Bandwidth:";
			strArray1[7] = this._mss.BandwidthHistory.GetAverageBandwidth().ToString("#,##;(#,##)");
			strArray1[8] = " bps Time:";
			strArray1[9] = this.media.Position.ToString("hh\\:mm\\:ss");
			string str2 = string.Concat(strArray1);
			txtBuffer.Text = str2;
			if (this._sortedAvailableVariants != null)
			{
				txtAvailable.Text = "Available: ";
				txtAvailable.Text += _sortedAvailableVariants.Count.ToString() + " - ";
				foreach (HLSVariant hlsVariant in this._sortedAvailableVariants)
				{
					txtAvailable.Text += hlsVariant.Bitrate.ToString("#,##;(#,##)") + " bps; ";
				}
			}
			if (this._bitrateCommand != BitrateCommand.DoNotChange)
				return;
		}

		public void SelectVariant(HLSVariant previousVariant, HLSVariant heuristicSuggestedVariant, ref HLSVariant nextVariant,
		                          List<HLSVariant> availableSortedVariants)
		{
			if (this._sortedAvailableVariants == null)
			{
				this._sortedAvailableVariants = new List<HLSVariant>(availableSortedVariants);
				for (var index1 = 0; index1 < this._sortedAvailableVariants.Count; ++index1)
				{
					for (var index2 = index1 + 1; index2 < this._sortedAvailableVariants.Count; ++index2)
					{
						Debug.Assert(this._sortedAvailableVariants[index1].ProgramId == this._sortedAvailableVariants[index2].ProgramId,
						             "The HLS Sample does not support playlists with different program IDs");
						if ((int)this._sortedAvailableVariants[index1].Bitrate == (int)this._sortedAvailableVariants[index2].Bitrate)
						{
							this._sortedAvailableVariants.RemoveAt(index2);
						}
					}
				}
				this._sortedAvailableVariants.Sort(new HLSVariantBitrateComparer());
				while (this._sortedAvailableVariants.Count > 0
				       && ((int)this._sortedAvailableVariants[0].Bitrate != 0 && this._sortedAvailableVariants[0].Bitrate < 100000U))
				{
					this._sortedAvailableVariants.RemoveAt(0);
				}
			}
			if (!this._sortedAvailableVariants.Contains(heuristicSuggestedVariant))
			{
				var index = 0;
				while (index < this._sortedAvailableVariants.Count - 1
				       &&
				       (heuristicSuggestedVariant.Bitrate >= this._sortedAvailableVariants[index].Bitrate
				        &&
				        (heuristicSuggestedVariant.Bitrate < this._sortedAvailableVariants[index].Bitrate
				         || heuristicSuggestedVariant.Bitrate >= this._sortedAvailableVariants[index + 1].Bitrate)))
				{
					++index;
				}
				heuristicSuggestedVariant = this._sortedAvailableVariants[index];
			}
			if (previousVariant == null)
			{
				nextVariant = heuristicSuggestedVariant;
			}
			else
			{
				switch (this._bitrateCommand)
				{
					case BitrateCommand.IncreaseBitrate:
						foreach (var hlsVariant in this._sortedAvailableVariants)
						{
							if (hlsVariant.Bitrate > previousVariant.Bitrate)
							{
								nextVariant = hlsVariant;
								break;
							}
						}
						this._bitrateCommand = BitrateCommand.DoNotChange;
						break;
					case BitrateCommand.DecreaseBitrate:
						foreach (var hlsVariant in this._sortedAvailableVariants)
						{
							if (hlsVariant.Bitrate < previousVariant.Bitrate)
							{
								nextVariant = hlsVariant;
							}
							else
							{
								break;
							}
						}
						this._bitrateCommand = BitrateCommand.DoNotChange;
						break;
					case BitrateCommand.Random:
						var random = new Random();
						nextVariant = this._sortedAvailableVariants[random.Next(0, this._sortedAvailableVariants.Count - 1)];
						break;
					case BitrateCommand.DoNotChange:
						nextVariant = previousVariant;
						break;
					case BitrateCommand.Auto:
						nextVariant = heuristicSuggestedVariant;
						break;
				}
			}
		}
	}
}