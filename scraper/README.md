# Are suggestions scraper

Scrapes archived resident suggestions from Åre kommun and writes one UTF-8 `.txt` file per suggestion.

## Run locally

```bash
dotnet run
```

Files are written to `scraper/output/` by default.

To choose another output directory:

```bash
dotnet run -- --output "/absolute/path/to/output"
```

## What it does

1. Fetches archived suggestions from `https://e-tjanster.are.se/forslag/archiveddata/301`
2. Fetches each suggestion detail page from `https://e-tjanster.are.se/forslag/show/{id}`
3. Extracts:
   - heading
   - sent in date
   - vote date
   - votes
   - status
   - description
   - attached files
4. Saves one text file per suggestion named `{id}.txt`

## Azure Function reuse

The scraping logic is already separated from file formatting in code, so the next step for Azure Functions is mainly to replace the local file write with another output target such as Blob Storage.
