using Microsoft.AspNetCore.Mvc;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Enums;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Api.Controllers;

[Route("api/humo")]
[Provider(ProcessingSystem.HUMO)]
public sealed class HumoController([FromKeyedServices(ProcessingSystem.HUMO)] IPaymentFlow service)
    : ProviderController(service);
