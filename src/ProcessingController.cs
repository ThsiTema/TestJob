using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace TestJob.Api;

[ApiController]
[Route("api/process")]
public sealed class ProcessingController(ProcessingService service) : ControllerBase
{
    /// <summary>Parse HTML, save selected elements, extract emails and decrypt AES-256 text.</summary>
    /// <remarks>
    /// Use json_payload_1.txt or json_payload_2.txt from the repository as the request body.
    /// HTML is taken from page_b64. The decoded URL is returned without fetching it.
    /// </remarks>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<ProcessingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProcessingResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProcessingResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ProcessingResponse>> Process(
        [FromBody, Required] ProcessingRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ProcessAsync(request, cancellationToken));
}
