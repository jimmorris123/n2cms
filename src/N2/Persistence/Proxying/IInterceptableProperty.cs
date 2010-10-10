﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace N2.Persistence.Proxying
{
	/// <summary>
	/// When added to an attribute this interface instructs the proxying system on how to deal with a property.
	/// </summary>
	public interface IInterceptableProperty
	{
		/// <summary>Describes how the property is stored.</summary>
		PropertyPersistenceOption StoreAs { get; }

		/// <summary>The default value of intercepted properties that have null value.</summary>
		object DefaultValue { get; }
	}
}
