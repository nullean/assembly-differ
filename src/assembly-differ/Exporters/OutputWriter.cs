using System;
using System.IO;

namespace Differ.Exporters
{
	public class OutputWriterFactory
	{
		private string _configuredPath;

		public OutputWriterFactory(string outputPath) => _configuredPath = outputPath;

		public OutputWriter Create(string defaultFileName = null)
		{
			// only write to console if no --output is provided
			if (string.IsNullOrEmpty(_configuredPath))
				return new OutputWriter();

			var filePath = Path.GetFullPath(_configuredPath);
			if (Directory.Exists(filePath))
			{
				if (defaultFileName == null)
					throw new Exception("output path is a directory but the current exporter"
						+ " does not define a default filename make sure --ouput points to file instead");
				filePath = Path.Combine(filePath, defaultFileName);
			}
			return new OutputWriter(filePath);

		}
	}

	public class OutputWriter : IDisposable
	{
		private readonly StreamWriter _writer;
		private readonly TextWriter _console;

		public OutputWriter() => _console = Console.Out;

		public OutputWriter(string outputFile) : this() => _writer = new StreamWriter(outputFile);

		public void WriteLine(string line)
		{
			_writer?.WriteLine(line);
			_console.WriteLine(line);
		}
		public void WriteLine()
		{
			_writer?.WriteLine();
			_console.WriteLine();
		}

		public void Write(string characters)
		{
			_writer?.Write(characters);
			_console.Write(characters);
		}

		public void Dispose() => _writer?.Dispose();

	}
}
