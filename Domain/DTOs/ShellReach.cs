namespace Domain.DTOs;

// Where a mount's shell runs a command: in a container that is part of the deployment, or on the
// machine of somebody who registered it. Null on a mount is no shell at all — which is not the
// same as no exec: /ha executes, and what it runs is a closed catalog of service calls.
public enum ShellReach
{
    Contained,
    Host
}