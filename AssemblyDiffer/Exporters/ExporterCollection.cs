using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Differ.Exporters
{
	public class ExporterCollection : KeyedCollection<string, IExporter>
	{
		protected override string GetKeyForItem(IExporter item) => item.Format;

		public ExporterCollection(params IExporter[] exporters)
		{
			if (exporters == null)
				throw new ArgumentNullException(nameof(exporters));

			foreach (var exporter in exporters)
				this.Add(exporter);
		}

		public string SupportedFormats => string.Join(", ", this.Select(e => e.Format));
	}
}
