using System;
using System.Linq;

namespace Differ.Providers.GitHub
{
	public class GitHubDiffCommand
	{
		public GitHubDiffCommand(string[] command)
		{
			if (command == null)
				throw new ArgumentNullException(nameof(command));

			if (command.Length < 4)
				throw new Exception("command must have a minimum length of 4");

			var ownerAndRepo = command[0].Split('/');

			if (ownerAndRepo.Length != 2)
				throw new Exception("first command value must be owner and repo in the form <owner>/<repo>");

			Owner = ownerAndRepo[0];
			Repo = ownerAndRepo[1];
			Commit = command[1];

			var commandAndArgs = command[2].Split(' ');

			BuildCommand = commandAndArgs[0];

			if (commandAndArgs.Length > 1)
				BuildArguments = commandAndArgs.Skip(1).ToArray();

			Output = command[3];
		}

		public string Owner { get; }

		public string Repo { get; }

		public string Commit { get; }

		public string BuildCommand { get; }

		public string[] BuildArguments { get; }

		public string Output { get; }

		public string TempDir { get; } = Environment.GetEnvironmentVariable("TEMP");
	}
}
