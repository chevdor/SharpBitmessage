﻿namespace bitmessage.network
{
	public class Verack : ICanBeSent
	{
		public string Command
		{
			get { return "verack"; }
		}

		public byte[] SentData
		{
			get { return null; }
		}
	}
}
