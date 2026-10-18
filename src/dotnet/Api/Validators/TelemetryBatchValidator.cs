using FluentValidation;
using Api.Models;

namespace Api.Validators;

public class TelemetryBatchValidator : AbstractValidator<TelemetryBatchRequest>
{
    public TelemetryBatchValidator()
    {
        RuleFor(x => x.Events).NotEmpty().WithMessage("Events batch cannot be empty");
        RuleForEach(x => x.Events).ChildRules(eventRule =>
        {
            eventRule.RuleFor(e => e.EventId).NotEmpty();
            eventRule.RuleFor(e => e.OccurredAt).NotEmpty();
            eventRule.RuleFor(e => e.MetricId).NotEmpty();
            eventRule.RuleFor(e => e.Status).NotEmpty();
        });
    }
}