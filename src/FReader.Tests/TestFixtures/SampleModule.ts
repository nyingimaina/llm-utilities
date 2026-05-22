import { Injectable } from "@angular/core";
import { Observable, of } from "rxjs";
import LocalCache from "../Caching/LocalCache";

export interface IUser {
  id: number;
  name: string;
  email?: string;
}

type Status = "active" | "inactive" | "pending";

export enum UserRole {
  Admin = "admin",
  User = "user",
  Guest = "guest",
}

export default class UserService {
  private baseUrl: string = "/api/users";
  private cache = new LocalCache();

  constructor(private readonly apiKey: string) {}

  public async getUsers(): Promise<IUser[]> {
    const cached = this.cache.get<IUser[]>("users");
    if (cached) return cached;
    const response = await fetch(this.baseUrl);
    return response.json();
  }

  public getUser(id: number): Observable<IUser | null> {
    return of(null);
  }

  private buildUrl(endpoint: string, params?: Record<string, string>): string {
    let url = `${this.baseUrl}/${endpoint}`;
    if (params) {
      const qs = Object.entries(params)
        .map(([k, v]) => `${k}=${encodeURIComponent(v)}`)
        .join("&");
      url += `?${qs}`;
    }
    return url;
  }

  get apiBaseUrl(): string {
    return this.baseUrl;
  }

  set apiBaseUrl(value: string) {
    this.baseUrl = value;
  }
}

export function mapUsers(users: IUser[]): string[] {
  return users.map((u) => u.name);
}

const defaultPageSize: number = 20;

export { UserService };
export type { IUser };
