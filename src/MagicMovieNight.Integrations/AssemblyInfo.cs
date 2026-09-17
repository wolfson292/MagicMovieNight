using System.Runtime.CompilerServices;

// Title splitting, column mapping and Arr error summarising are implementation
// details, but they are the
// parts most likely to break on a new export format — so the tests reach them directly
// rather than only through the public streaming API.
[assembly: InternalsVisibleTo("MagicMovieNight.Tests")]
