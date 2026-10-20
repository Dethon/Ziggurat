namespace Domain.Agents;

// What the model one send resolved to can take beside text, asked per send rather than per agent:
// a config patch can move a single turn onto a model that takes less than the agent's own does.
// The model is named so a refusal can say which one is refusing.
public sealed record ModelInputs(string Model, bool Images, bool Video)
{
    public static ModelInputs Everything(string model) => new(model, true, true);
}