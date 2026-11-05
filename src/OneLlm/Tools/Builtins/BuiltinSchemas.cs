// The built-in tools share their names and argument shapes with existing agent definitions.
namespace OneLlm.Tools.Builtins;

/// <summary>The names, descriptions and input schemas of the built-in tools, as JSON. The names and argument shapes are the ones existing agent definitions already use, so they run unchanged.</summary>
internal static class BuiltinSchemas
{
    public const string ReadFileDescription = """"
Read a file or list one level of a directory at the specified path. Directory listings contain at most 50 entries, with directories marked by a trailing slash and file sizes shown. Relative paths are resolved from the current working directory. Can also fetch content from an HTTP/HTTPS URL. Image and video results (jpg, png, gif, webp, mp4, webm, mov, etc.) are returned to agents as asset message blocks. IMPORTANT: when you need to read many files, read them all at once in parallel rather than one at a time — up to 1000 parallel read_file calls are supported.
"""";

    public const string ReadFileSchema = """"
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "The file or directory path to read (absolute or relative to working directory), or an HTTP/HTTPS URL to fetch. Directories return a one-level listing of at most 50 entries."
    },
    "includeLineNumbers": {
      "type": "boolean",
      "description": "If true, each line of text content is prefixed with a right-aligned line number and \" | \" separator (e.g. \"   1 | first line\"). The gutter is at least 4 characters wide, growing to fit the largest line number. Ignored for images and binary files. Defaults to false.",
      "default": false
    }
  },
  "required": [
    "path"
  ]
}
"""";

    public const string WriteFileDescription = """"
Write content to a file at the specified path. Automatically creates parent directories if needed. Can append to existing files. Relative paths are resolved from the current working directory. Returns a plain text summary of the operation.
"""";

    public const string WriteFileSchema = """"
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "The file path to write to (absolute or relative to working directory)"
    },
    "content": {
      "type": "string",
      "description": "The content to write"
    },
    "append": {
      "type": "boolean",
      "description": "If true, append to existing file instead of overwriting",
      "default": false
    }
  },
  "required": [
    "path",
    "content"
  ]
}
"""";

    public const string ReplaceStringDescription = """"
Replace all occurrences of find strings with replacement strings in a file. Supports multiple replacements in a single operation. Relative paths are resolved from the current working directory. Automatically handles different encodings (UTF-8, UTF-16, ISO-8859-1) and line endings (CRLF/LF). Line endings are normalized for matching, so a find with \n will match both \n and \r\n in the file. Returns a plain text summary of the operation.

If exact matching yields zero occurrences for a given find, the routine retries with a fuzzy fallback that collapses runs of 4+ separator glyphs (dashes, box-drawing horizontals, '=', '_', '*', '~', '.', and visually similar variants) to a canonical 4-character run on both sides. The fuzzy match only fires when exactly one collapsed match exists — it refuses to guess between multiple ambiguous regions. Fuzzy matches are marked in the response so the caller can verify.
"""";

    public const string ReplaceStringSchema = """"
{
  "type": "object",
  "properties": {
    "filePath": {
      "type": "string",
      "description": "The file path to perform replacement on (absolute or relative to working directory)"
    },
    "replacements": {
      "type": "array",
      "description": "Array of search/replace operations to perform in order",
      "items": {
        "type": "object",
        "properties": {
          "find": {
            "type": "string",
            "description": "The EXISTING text currently in the file that you want to change.\nThis must match what is already present. Think of this as the \"before\" / \"old\" content.\nLine endings are normalized, so \\n matches both \\n and \\r\\n.\n"
          },
          "replace": {
            "type": "string",
            "description": "The NEW text that should REPLACE the existing text.\nThink of this as the \"after\" / \"new\" content. Can be empty to delete.\nLine endings in the replacement will be converted to match the file's original style.\n"
          }
        },
        "required": [
          "find",
          "replace"
        ]
      }
    }
  },
  "required": [
    "filePath",
    "replacements"
  ]
}
"""";

    public const string ListFilesDescription = """"
List files and directories in a specified directory. Returns paths relative to the target directory.

For agents: Returns a visual directory tree (text format) for easy reading.
For API calls: Returns structured JSON with file metadata.

Supports:
- Depth limiting (maxChildDepth: 0 = only immediate children)
- Exclusion patterns (glob patterns to skip)
- Inclusion patterns (glob patterns to match - if specified, only matching files are returned)
- Gitignore integration (automatically respects .gitignore files in the target directory, its subdirectories, and its ancestor directories up to the nearest enclosing .git repository root)
- Symlinks are classified through the link: a symlink to a directory reports isDir true (a symlink to a file reports the target's size), plus isSymlink/linkTarget fields. Symlinked directories are never recursed into (cycle-safe).

Pattern syntax:
- `*.ext` - Match files with extension
- `name` - Match exact name at any level
- `**/name` - Match name at any depth
- `dir/` - Match directories only
- `/path` - Anchored to root of search directory
"""";

    public const string ListFilesSchema = """"
{
  "type": "object",
  "properties": {
    "directory": {
      "type": "string",
      "description": "Directory path to list (absolute or relative to working directory). Defaults to working directory if not specified."
    },
    "maxChildDepth": {
      "type": "integer",
      "description": "Maximum depth to recurse into subdirectories. 0 = only immediate children, 1 = one level deep, etc. Use -1 for unlimited depth.",
      "default": -1
    },
    "exclusionPatterns": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Glob patterns for files/directories to exclude (e.g., [\"*.log\", \"node_modules/\"]). Note: .git/ is always excluded regardless of this list."
    },
    "inclusionPatterns": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Glob patterns for files to include. If specified, only matching files are returned. Directories are always traversed."
    },
    "excludeGitIgnoreMatches": {
      "type": "boolean",
      "description": "If true, exclude files matching patterns in .gitignore files. Looks in the target directory, its subdirectories, and its ancestor directories (following the real path when the target is a symlink). Ancestor lookup stops at the nearest enclosing .git repository root.",
      "default": true
    },
    "omitEmptyDirs": {
      "type": "boolean",
      "description": "If true, omit directories that contain no files (or no files matching inclusion patterns). Useful for keeping results clean when filtering.",
      "default": false
    },
    "includeModified": {
      "type": "boolean",
      "description": "If true, include each entry's last-modified time (RFC3339 UTC) in the output. File sizes are always included; only the timestamp is gated by this flag.",
      "default": false
    }
  },
  "required": []
}
"""";

    public const string SearchFilesRegexDescription = """"
Search for files in a specified directory using a regular expression pattern.
This tool can search file names only, or both file names and contents.
When searching contents, only text files are searched, and a maximum of 5 results per file are returned.
Files larger than 10MB are skipped when searching contents.

By default the search skips common build/output directories (.git/, node_modules/,
__pycache__/, venv/, .venv/) and anything matched by .gitignore in the target
directory or its ancestors up to the nearest enclosing git repository root.
Provide your own `exclusion_patterns` to override the defaults (use [] to disable),
or set `exclude_gitignore_matches: false` to ignore .gitignore.
"""";

    public const string SearchFilesRegexSchema = """"
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Directory or file path to search in (absolute or relative to working directory)"
    },
    "pattern": {
      "type": "string",
      "description": "Regular expression pattern to search for"
    },
    "search_file_names_only": {
      "type": "boolean",
      "description": "If true, only search file names, not contents",
      "default": false
    },
    "context_lines_before": {
      "type": "integer",
      "description": "Number of context lines to show before each match (default: 3 for single file, 0 for directory)"
    },
    "context_lines_after": {
      "type": "integer",
      "description": "Number of context lines to show after each match (default: 3 for single file, 0 for directory)"
    },
    "exclusion_patterns": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Glob patterns for files/directories to exclude (e.g. [\"*.log\", \"node_modules/\", \".git/\"]).\nWhen omitted, defaults to [\".git/\", \"node_modules/\", \"__pycache__/\", \"venv/\", \".venv/\"].\nPass [] to disable defaults entirely. Only applied to directory searches.\n"
    },
    "inclusion_patterns": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Glob patterns for files to include. If specified, only files matching at least\none pattern are searched (e.g. [\"*.go\", \"**/*.md\"]). Directories are always\ntraversed so matching files can be found inside them. Only applied to directory searches.\n"
    },
    "exclude_gitignore_matches": {
      "type": "boolean",
      "description": "If true (the default), exclude files and directories matching patterns in\n.gitignore files. Looks in the target directory, its subdirectories, and\nits ancestor directories up to the nearest enclosing .git repository root.\nOnly applied to directory searches.\n",
      "default": true
    }
  },
  "required": [
    "path",
    "pattern"
  ]
}
"""";

    public const string ExecuteTerminalCommandDescription = """"
Execute a terminal command and return the output. Commands run in the current working directory, unless overridden by the working_dir parameter. Returns stdout/stderr as plain text. Captures at most 10 KB per stream, retaining the beginning and end with an elision marker when truncated.
"""";

    public const string ExecuteTerminalCommandSchema = """"
{
  "type": "object",
  "properties": {
    "command": {
      "type": "string",
      "description": "The command to execute"
    },
    "args": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Command arguments"
    },
    "working_dir": {
      "type": "string",
      "description": "Override working directory for this command (absolute or relative to current working directory)"
    },
    "timeout": {
      "type": "integer",
      "description": "Timeout in seconds (default: 30, max: 3600). Larger values are capped at 3600 and the result includes a note saying so.",
      "default": 30
    },
    "env": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Environment variables in KEY=VALUE format"
    }
  },
  "required": [
    "command"
  ]
}
"""";

    public const string FetchWebpageMarkdownDescription = """"
Fetch a webpage and convert it to markdown format. Useful for extracting readable content from web pages for analysis or processing. Returns the markdown content as plain text.
"""";

    public const string FetchWebpageMarkdownSchema = """"
{
  "type": "object",
  "properties": {
    "url": {
      "type": "string",
      "description": "The URL of the webpage to fetch and convert to markdown"
    }
  },
  "required": [
    "url"
  ]
}
"""";

    public const string SearchWebDescription = """"
Search the web using Brave Search API. Returns web results and optionally video results for the given query. Returns results as plain text.
"""";

    public const string SearchWebSchema = """"
{
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "The search query"
    },
    "count": {
      "type": "integer",
      "description": "Number of results to return (default: 10, max: 20)"
    }
  },
  "required": [
    "query"
  ]
}
"""";

}
