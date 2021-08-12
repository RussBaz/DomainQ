namespace DomainQ

module Say =
    let hello name =
        sprintf $"Hello %s{name}"