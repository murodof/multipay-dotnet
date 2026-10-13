using Microsoft.AspNetCore.Mvc;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Enums;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Api.Controllers;

[Route("api/uzcard")]
[Provider(ProcessingSystem.UZCARD)]
public sealed class UzcardController([FromKeyedServices(ProcessingSystem.UZCARD)] IPaymentFlow service)
    : ProviderController(service);
