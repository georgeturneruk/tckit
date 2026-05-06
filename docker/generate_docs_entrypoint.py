"""Entrypoint for the docs compose service.

Generates HTML docs then serves them on port 8080.
"""
import os
import sys

from tckit.adapters.doc_generators.html_generator import HtmlGenerator

project_path = os.environ.get("PLC_PROJECT_PATH", "/project")
output_path = os.environ.get("DOCS_OUTPUT_PATH", "/docs-output")

print(f"Generating docs from {project_path} → {output_path}")
result = HtmlGenerator().generate(project_path, output_path)
print(result)

if not result.success:
    sys.exit(1)

print(f"\nDocs built. Serving at http://localhost:8080\n")
print(f"index.html → {result.details.get('index', output_path + '/index.html')}")

import http.server
import os as _os

_os.chdir(output_path)
handler = http.server.SimpleHTTPRequestHandler
httpd = http.server.HTTPServer(("0.0.0.0", 8080), handler)
httpd.serve_forever()
